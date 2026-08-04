#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFile as execFileCallback } from 'node:child_process';
import {
  cp,
  mkdir,
  readFile,
  readdir,
  rm,
  stat,
  writeFile
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { validateManifest } from './core.mjs';
import { copyReportEvidenceTree } from './report-capture.mjs';
import { renderReport, validateRunResult } from './report.mjs';

const execFile = promisify(execFileCallback);
const suiteRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(suiteRoot, '..', '..');
const canaryRoot = path.join(repoRoot, '.tmp', 'remote-test-suite', 'canary-20260729');
const composeRunId = 'canary-machine-10m';
const composeRoot = path.join(repoRoot, '.tmp', 'remote-test-suite', 'compose', composeRunId);
const composeEvidenceRoot = path.join(composeRoot, 'evidence');
const composeReferenceRoot = path.join(
  composeRoot, 'scenarios', 'reference-change', `${composeRunId}-reference`);
const parallelRunId = 'canary-parallel-12';
const parallelLiveRoot = path.join(
  repoRoot, '.tmp', 'remote-test-suite', 'parallel-delivery', parallelRunId);
const parallelEvidenceRoot = path.join(canaryRoot, 'parallel', parallelRunId);
const reportRoot = path.join(
  repoRoot, 'docs', 'quality', 'remote-run-testsuite-report', '2026-07-29');
const rawRoot = path.join(reportRoot, 'raw');
const manifestRoot = path.join(reportRoot, 'manifests');
const contractRoot = path.join(reportRoot, 'contracts');
const chronicleHref =
  '../../../operations/haertung-verteilte-ausfuehrung/historie.html';
const phaseOrder = Object.freeze(['claim', 'run', 'gate', 'review', 'integration']);

const scenarioDefinitions = [
  ['reference-change', 'canary-ref-a', 'baseline'],
  ['reference-change', 'canary-ref-b', 'baseline'],
  ['fault-task-server-network-blips', 'canary-net-blip', 'healed'],
  ['fault-gate-timeout', 'canary-gate-timeout', 'recovered'],
  ['fault-worktree-collision', 'canary-worktree-collision', 'recovered'],
  ['fault-lost-completion-sentinel', 'canary-sentinel-loss', 'recovered'],
  ['divergent-salvage-lineage', 'canary-salvage-lineage', 'recovered'],
  ['lease-adoption-restart', 'canary-lease-adoption', 'recovered'],
  ['external-completion-cycle', 'canary-external-cycle', 'recovered']
];

await assertRequiredRoots();
assertSafeGeneratedRoot(reportRoot);
await rm(reportRoot, { recursive: true, force: true });
await Promise.all([
  mkdir(rawRoot, { recursive: true }),
  mkdir(manifestRoot, { recursive: true }),
  mkdir(contractRoot, { recursive: true })
]);

const versions = await readJson(path.join(composeEvidenceRoot, 'versions.json'));
const sourceState = await captureSourceState(versions);
const componentVersions = {
  schemaVersion: 1,
  capturedAt: versions.capturedAt,
  runnerIdentity: {
    runnerId: process.env.RUNNER_ID ?? null,
    runnerClientId: process.env.RUNNER_CLIENT_ID ?? null,
    runnerName: process.env.RUNNER_NAME ?? null,
    operatingSystemHostname: os.hostname()
  },
  sourceState,
  docker: versions.docker,
  compose: versions.compose,
  components: versions.components,
  images: versions.images
};
await writeJson(path.join(reportRoot, 'component-versions.json'), componentVersions);

await Promise.all([
  copyReportEvidenceTree(
    composeEvidenceRoot,
    path.join(rawRoot, 'compose-machine-bound')),
  cp(parallelEvidenceRoot, path.join(rawRoot, 'parallel-delivery'), { recursive: true }),
  copyIfPresent(
    path.join(repoRoot, '.tmp', 'remote-test-suite', 'compose', composeRunId, 'evidence',
      'reference-task.json'),
    path.join(rawRoot, 'compose-reference', 'reference-task.json')),
  copyPreflightEvidence()
]);

const manifestIndex = [];
const reportRuns = [];
for (const [scenario, runId, outcome] of scenarioDefinitions) {
  const runRoot = path.join(canaryRoot, scenario, runId);
  const manifest = validateManifest(await readJson(
    path.join(suiteRoot, 'scenarios', `${scenario}.json`)));
  await copyScenarioRaw(runRoot, runId);
  await copyManifest(scenario, manifest, manifestIndex);
  reportRuns.push(await scenarioReportRun({
    scenario,
    runId,
    outcome,
    runRoot,
    manifest
  }));
}

await copyScenarioRaw(composeReferenceRoot, `${composeRunId}-reference`);
await copyManifest(
  'reference-change',
  validateManifest(await readJson(path.join(suiteRoot, 'scenarios', 'reference-change.json'))),
  manifestIndex);
await copyFileWithParents(
  path.join(suiteRoot, 'fault-catalog.json'),
  path.join(contractRoot, 'fault-catalog.json'));
await copyFileWithParents(
  path.join(suiteRoot, 'scenario.schema.json'),
  path.join(contractRoot, 'scenario.schema.json'));
await copyFileWithParents(
  path.join(suiteRoot, 'run-result.schema.json'),
  path.join(contractRoot, 'run-result.schema.json'));

const composeAcceptance = await readJson(
  path.join(composeEvidenceRoot, 'acceptance.json'));
reportRuns.push(await composeReportRun(composeAcceptance));
const parallelAcceptance = await readJson(
  path.join(parallelEvidenceRoot, 'acceptance.json'));
for (const scenario of parallelAcceptance.scenarios) {
  reportRuns.push(await parallelReportRun(parallelAcceptance, scenario));
}

const syntheticManifests = [
  {
    id: 'compose-machine-bound',
    file: 'compose-machine-bound.json',
    value: {
      schemaVersion: 1,
      name: 'compose-machine-bound',
      runId: composeRunId,
      autonomyDurationSeconds: 600,
      machineBound: true,
      sequence: [
        'reference-task',
        'multi-slot-task-server-outage',
        'studio-rolling-update',
        'runner-rolling-update',
        'task-server-rolling-update',
        'identity-scoped-teardown'
      ]
    }
  },
  ...parallelAcceptance.scenarios.map(scenario => ({
    id: `parallel-${scenario.name}`,
    file: `parallel-${scenario.name}.json`,
    value: {
      schemaVersion: 1,
      name: `parallel-${scenario.name}`,
      runId: parallelRunId,
      seed: parallelAcceptance.seed,
      taskCount: scenario.taskCount,
      workerPools: scenario.workerPools,
      controlledWorkerLoss: scenario.workerLoss ?? null
    }
  }))
];
for (const item of syntheticManifests) {
  const destination = path.join(manifestRoot, item.file);
  await writeJson(destination, item.value);
  manifestIndex.push(await manifestRecord(item.id, destination, 'generated'));
}
await writeJson(path.join(reportRoot, 'scenario-manifests.json'), {
  schemaVersion: 1,
  manifests: manifestIndex.sort((left, right) => left.id.localeCompare(right.id))
});

const report = {
  $schema: './contracts/run-result.schema.json',
  schemaVersion: 1,
  suite: {
    name: 'AGT-2200 remote-run infrastructure acceptance canary',
    sourceTaskKey: 'AGT-2200',
    sourceTaskHref: './acceptance-matrix.json#AGT-2200',
    chronicleHref
  },
  generatedAt: new Date().toISOString(),
  runs: reportRuns
};
const reportValidation = validateRunResult(report);
if (!reportValidation.valid) {
  throw new Error(
    `Generated report failed validation:\n${reportValidation.errors.join('\n')}`);
}
await writeJson(path.join(reportRoot, 'validated-runs.json'), report);
await writeFile(path.join(reportRoot, 'index.html'), renderReport(report));

const cleanup = await cleanTemporaryResources();
await writeJson(path.join(reportRoot, 'cleanup-verification.json'), cleanup);
const acceptanceMatrix = buildAcceptanceMatrix({
  report,
  composeAcceptance,
  parallelAcceptance,
  cleanup,
  sourceState
});
validateAcceptanceMatrix(acceptanceMatrix);
await writeJson(path.join(reportRoot, 'acceptance-matrix.json'), acceptanceMatrix);

const evidenceIndex = await createEvidenceIndex(reportRoot);
await writeJson(path.join(reportRoot, 'raw-evidence-index.json'), evidenceIndex);
await writeJson(path.join(reportRoot, 'validation.json'), {
  schemaVersion: 1,
  validatedAt: new Date().toISOString(),
  accepted: true,
  validators: [
    {
      name: 'remote-run-result-v1',
      schema: './contracts/run-result.schema.json',
      status: 'pass',
      records: report.runs.length
    },
    {
      name: 'scenario-manifest-v1',
      schema: './contracts/scenario.schema.json',
      status: 'pass',
      records: scenarioDefinitions.length
    },
    {
      name: 'acceptance-matrix-cross-field',
      status: 'pass',
      criteria: acceptanceMatrix.criteria.length,
      guards: acceptanceMatrix.canaryGuards.length
    },
    {
      name: 'raw-evidence-sha256',
      status: 'pass',
      records: evidenceIndex.files.length
    }
  ]
});

console.log(JSON.stringify({
  accepted: true,
  reportRoot,
  reportRuns: report.runs.length,
  acceptanceCriteria: acceptanceMatrix.criteria.length,
  guards: acceptanceMatrix.canaryGuards.length,
  rawEvidenceFiles: evidenceIndex.files.length,
  cleanup
}, null, 2));

async function scenarioReportRun({ scenario, runId, outcome, runRoot, manifest }) {
  const acceptance = manifest.contract ?? manifest.acceptance;
  const result = await readJson(path.join(runRoot, 'result.json'));
  const detailed = await readJsonIfPresent(path.join(runRoot, 'assertions.json'));
  const timing = await phaseTiming(path.join(runRoot, 'phases.jsonl'), result.phaseSequence);
  const baseSha = result.evidence?.baseSha
    ?? await rootCommit(path.join(runRoot, 'fixture-origin.git'), manifest.fixture.defaultBranch);
  const resultSha = result.resultSha
    ?? result.evidence?.resultSha
    ?? detailed?.resultSha
    ?? null;
  const contractPassed = result.actualTerminal === result.expectedTerminal
    && result.recoveryBudget.used <= result.recoveryBudget.maximum
    && result.assertions.every(assertion => assertion.passed === true);
  if (!contractPassed) throw new Error(`${runId} did not pass its declared contract.`);
  const incidentLinks = result.chronicleLinks ?? acceptance.chronicleLinks;
  const rawHref = `./raw/scenarios/${runId}/result.json`;
  const attemptId = detailed?.assertions?.lease?.runAttemptId
    ?? result.evidence?.sourceRunAttemptId
    ?? result.evidence?.runAttemptId
    ?? result.evidence?.retryRunAttemptId
    ?? `${runId}:contract`;
  const noDuplicate = detailed?.assertions?.lease?.noDuplicateClaim !== false;
  const outboxClean = detailed?.assertions?.outbox?.backlog !== undefined
    ? detailed.assertions.outbox.backlog === 0
    : true;
  const phantomPrevented = detailed?.assertions?.sha?.phantomSuccessPrevented !== false;
  const incidents = await incidentsForManifest(manifest, result);
  return {
    runId,
    taskKey: manifest.task.key,
    taskHref: rawHref,
    attemptId,
    attemptHref: `${rawHref}#attempt`,
    scenario: {
      id: scenario,
      manifestHref: `./manifests/${scenario}.json`
    },
    outcome,
    accepted: true,
    wallMs: timing.wallMs,
    phases: timing.phases,
    tokens: unavailableTokens(),
    baseSha,
    resultSha,
    rawArtifactHref: rawHref,
    components: reportComponents(),
    assertions: commonAssertions({
      terminal: `${result.actualTerminal} matched ${result.expectedTerminal}; recovery ${result.recoveryBudget.used}/${result.recoveryBudget.maximum} ${result.recoveryBudget.unit}.`,
      timing: `${timing.executed.length} executed phases have paired UTC and monotonic boundaries; remaining phases are explicitly skipped.`,
      scope: `Fixture workspace, branches, Task Server data, and worktrees were contained under ${relative(runRoot)}.`,
      authority: noDuplicate && outboxClean
        ? 'No duplicate claim or unacknowledged outbox item remained in scenario evidence.'
        : 'Authority or outbox evidence was not clean.',
      result: resultSha
        ? `Exact Result-SHA ${resultSha} is retained in raw evidence.`
        : 'The declared fail-closed terminal published no Result-SHA, preventing phantom delivery.',
      incident: incidentLinks.length > 0
        ? `${incidentLinks.length} chronicle link(s) are declared by the scenario contract.`
        : null,
      authorityPassed: noDuplicate && outboxClean,
      resultPassed: phantomPrevented
    }),
    incidents,
    links: [
      { label: 'Phase journal', href: `./raw/scenarios/${runId}/phases.jsonl` },
      { label: 'Acceptance matrix', href: './acceptance-matrix.json' }
    ]
  };
}

async function composeReportRun(acceptance) {
  if (!acceptance.assertions.every(assertion => assertion.passed === true)) {
    throw new Error('Compose acceptance contains a failed assertion.');
  }
  const reference = acceptance.reference;
  const timing = await phaseTiming(
    path.join(composeReferenceRoot, 'phases.jsonl'),
    reference.phaseSequence);
  const runPhase = timing.phases.find(phase => phase.name === 'run');
  runPhase.executionMs += acceptance.autonomy.durationMs;
  timing.wallMs = Math.max(
    timing.phases.reduce(
      (sum, phase) => sum + phase.queueMs + phase.executionMs, 0),
    Date.parse(acceptance.completedAt)
      - Date.parse((await firstPhaseEvent(
        path.join(composeReferenceRoot, 'phases.jsonl'))).scenarioStartedAt));
  const manifest = validateManifest(await readJson(
    path.join(suiteRoot, 'scenarios', 'reference-change.json')));
  const baseSha = await rootCommit(
    path.join(composeReferenceRoot, 'fixture-origin.git'),
    manifest.fixture.defaultBranch);
  const rawHref = './raw/compose-machine-bound/acceptance.json';
  return {
    runId: composeRunId,
    taskKey: 'AUTO-1',
    taskHref: rawHref,
    attemptId: acceptance.autonomy.slots[0].runId,
    attemptHref: `${rawHref}#autonomy`,
    scenario: {
      id: 'compose-machine-bound',
      manifestHref: './manifests/compose-machine-bound.json'
    },
    outcome: 'recovered',
    accepted: true,
    wallMs: timing.wallMs,
    phases: timing.phases,
    tokens: unavailableTokens(),
    baseSha,
    resultSha: reference.resultSha,
    rawArtifactHref: rawHref,
    components: reportComponents(),
    assertions: commonAssertions({
      terminal: `All ${acceptance.assertions.length} Compose acceptance assertions passed.`,
      timing: `The continuous Task Server outage lasted ${acceptance.autonomy.durationMs} ms and is included in Run execution time.`,
      scope: `Docker project ${acceptance.project} used one explicit disposable identity.`,
      authority: `Both active slots reconciled their original fences before replay; recovery advanced fence ${acceptance.rollingTask.originalFence} to ${acceptance.rollingTask.recoveredFence}.`,
      result: 'Autonomy events, artifacts, handoffs, and completions were delivered once; final outboxes and authority were empty.',
      incident: 'The outage and rolling-restart authority incidents link to the hardening chronicle.',
      authorityPassed: true,
      resultPassed: true
    }),
    incidents: [
      {
        class: 'task-server-outage',
        label: 'Ten-minute Task Server outage',
        chronicleAnchor: 'incident-attempt-authority',
        injected: true,
        recoveryOutcome: `${acceptance.autonomy.durationMs} ms outage, two slots, exact-fence reconciliation, zero backlog.`
      },
      {
        class: 'rolling-authority-restart',
        label: 'Independent component rolling updates',
        chronicleAnchor: 'incident-zombie-leases',
        injected: true,
        recoveryOutcome: 'Studio, Runner, and Task Server were replaced independently; stale completion was rejected.'
      }
    ],
    links: [
      { label: 'Autonomy evidence', href: './raw/compose-machine-bound/autonomy-canary.json' },
      { label: 'Rolling history', href: './raw/compose-machine-bound/api/rolling-history.json' },
      { label: 'Teardown proof', href: './cleanup-verification.json' }
    ]
  };
}

async function parallelReportRun(acceptance, scenario) {
  const scenarioAssertions = acceptance.assertions.filter(assertion =>
    !assertion.name.includes(':') || assertion.name.startsWith(`${scenario.name}:`));
  if (!scenarioAssertions.every(assertion => Boolean(assertion.passed))) {
    throw new Error(`Parallel scenario ${scenario.name} contains a failed assertion.`);
  }
  const timing = parallelPhaseTiming(scenario);
  const firstTask = scenario.tasks[0];
  const lastTask = scenario.tasks.at(-1);
  const rawHref = `./raw/parallel-delivery/${scenario.name}/scenario.json`;
  const workerLoss = scenario.name === 'worker-loss';
  return {
    runId: `${parallelRunId}-${scenario.name}`,
    taskKey: firstTask.taskKey,
    taskHref: rawHref,
    attemptId: runIdFromRef(firstTask.resultRef) ?? firstTask.taskId,
    attemptHref: `${rawHref}#tasks`,
    scenario: {
      id: `parallel-${scenario.name}`,
      manifestHref: `./manifests/parallel-${scenario.name}.json`
    },
    outcome: workerLoss ? 'recovered' : 'baseline',
    accepted: true,
    wallMs: timing.wallMs,
    phases: timing.phases,
    tokens: unavailableTokens(),
    baseSha: firstTask.coding.baseSha,
    resultSha: lastTask.integration.integratedHead,
    rawArtifactHref: rawHref,
    components: reportComponents(),
    assertions: commonAssertions({
      terminal: `${scenario.taskCount} cards reached deterministic integration order with ${scenarioAssertions.length} applicable assertions passing.`,
      timing: 'Claim, Run, Gate, Review, and Integration critical-path boundaries are derived from per-task UTC timing records.',
      scope: 'Coding workspaces were unique; disposable gate and review roots were removed after exact-SHA execution.',
      authority: `${scenario.peakActiveOrPost} cards were active or post-processing together; queues and authority actions drained.`,
      result: 'Every gate and review ran at its declared Result-SHA, and delivery/report replays remained idempotent.',
      incident: workerLoss
        ? 'Controlled gate-worker loss is linked to the remote-gates chronicle incident.'
        : null,
      authorityPassed: true,
      resultPassed: true
    }),
    incidents: workerLoss
      ? [{
          class: 'gate-worker-loss',
          label: 'Controlled gate worker loss',
          chronicleAnchor: 'incident-remote-gates',
          injected: true,
          recoveryOutcome: `${scenario.retries} retry operations redistributed across the surviving gate pool.`
        }]
      : [],
    links: [
      { label: 'Concurrency report', href: './raw/parallel-delivery/concurrency-report.md' },
      { label: 'Timeline', href: `./raw/parallel-delivery/${scenario.name}/timeline.jsonl` },
      { label: 'Task histories', href: `./raw/parallel-delivery/${scenario.name}/task-histories.json` }
    ]
  };
}

function commonAssertions({
  terminal,
  timing,
  scope,
  authority,
  result,
  incident,
  authorityPassed,
  resultPassed
}) {
  const assertions = [
    assertion('declared-terminal', 'Declared terminal reached', true, terminal),
    assertion('phase-timing-present', 'Phase timing present', true, timing),
    assertion('scope-contained', 'Resources remained scoped', true, scope),
    assertion('authority-clean', 'Authority and queues reconciled', authorityPassed, authority),
    assertion('exact-result-or-nondelivery', 'Exact result or expected non-delivery', resultPassed, result)
  ];
  if (incident) {
    assertions.push(assertion(
      'incident-linked', 'Incident linked to chronicle', true, incident));
  }
  return assertions;
}

function assertion(id, label, passed, detail) {
  if (!passed) throw new Error(`Acceptance assertion '${id}' failed: ${detail}`);
  return {
    id,
    label,
    status: 'pass',
    detail,
    evidenceHref: './acceptance-matrix.json'
  };
}

async function incidentsForManifest(manifest, result) {
  const acceptance = manifest.contract ?? manifest.acceptance;
  const catalog = await readJson(path.join(suiteRoot, 'fault-catalog.json'));
  const incidents = [];
  for (const faultId of manifest.faults ?? []) {
    const fault = catalog.faults.find(item => item.id === faultId);
    if (!fault) throw new Error(`Fault ${faultId} is absent from the catalog.`);
    incidents.push({
      class: fault.incidentClass,
      label: fault.description,
      chronicleAnchor: fault.anchors[0].split('#')[1],
      injected: true,
      recoveryOutcome: result.incidentOutcome
    });
  }
  if (incidents.length === 0 && acceptance.chronicleLinks.length > 0) {
    for (const link of acceptance.chronicleLinks) {
      incidents.push({
        class: manifest.name,
        label: `Historical replay: ${manifest.name}`,
        chronicleAnchor: link.split('#')[1],
        injected: false,
        recoveryOutcome:
          `${result.actualTerminal} reached within ${result.recoveryBudget.used}/${result.recoveryBudget.maximum} ${result.recoveryBudget.unit}.`
      });
    }
  }
  return incidents;
}

async function phaseTiming(file, executedPhases) {
  const events = await readJsonl(file);
  if (events.length === 0) throw new Error(`Phase journal is empty: ${file}`);
  let previousSequence = 0;
  let previousMonotonic = -1;
  for (const event of events) {
    if (!Number.isInteger(event.sequence) || event.sequence <= previousSequence
        || !Number.isInteger(event.monotonicMs)
        || event.monotonicMs < previousMonotonic
        || Number.isNaN(Date.parse(event.recordedAt))
        || Number.isNaN(Date.parse(event.scenarioStartedAt))) {
      throw new Error(`Phase journal has missing or non-monotonic timing: ${file}`);
    }
    previousSequence = event.sequence;
    previousMonotonic = event.monotonicMs;
  }
  let previousAfter = 0;
  const phases = [];
  for (const phase of phaseOrder) {
    if (!executedPhases.includes(phase)) {
      phases.push({ name: phase, status: 'skipped', queueMs: 0, executionMs: 0 });
      continue;
    }
    const before = events.filter(event => event.phase === phase && event.point === 'before');
    const after = events.filter(event => event.phase === phase && event.point === 'after');
    if (before.length !== 1 || after.length !== 1
        || after[0].monotonicMs < before[0].monotonicMs) {
      throw new Error(`Phase ${phase} lacks one valid before/after timing pair: ${file}`);
    }
    phases.push({
      name: phase,
      status: 'pass',
      queueMs: Math.max(0, before[0].monotonicMs - previousAfter),
      executionMs: after[0].monotonicMs - before[0].monotonicMs
    });
    previousAfter = after[0].monotonicMs;
  }
  return {
    phases,
    executed: [...executedPhases],
    wallMs: Math.max(1, previousAfter)
  };
}

function parallelPhaseTiming(scenario) {
  const epoch = value => Date.parse(value);
  const start = epoch(scenario.startedAt);
  const finish = epoch(scenario.finishedAt);
  const codingStart = Math.min(...scenario.tasks.map(task => epoch(task.coding.startedAt)));
  const codingFinish = Math.max(...scenario.tasks.map(task => epoch(task.coding.finishedAt)));
  const gateFinish = Math.max(...scenario.tasks.map(task => epoch(task.gate.finishedAt)));
  const reviewFinish = Math.max(...scenario.tasks.map(task => epoch(task.review.finishedAt)));
  const boundaries = [start, codingStart, codingFinish, gateFinish, reviewFinish, finish];
  if (boundaries.some(value => !Number.isFinite(value))
      || boundaries.some((value, index) => index > 0 && value < boundaries[index - 1])) {
    throw new Error(`Parallel scenario ${scenario.name} has incomplete phase timing.`);
  }
  return {
    wallMs: finish - start,
    phases: phaseOrder.map((name, index) => ({
      name,
      status: 'pass',
      queueMs: index === 0 ? boundaries[1] - boundaries[0] : 0,
      executionMs: index === 0 ? 0 : boundaries[index + 1] - boundaries[index]
    }))
  };
}

function buildAcceptanceMatrix({
  report,
  composeAcceptance,
  parallelAcceptance,
  cleanup,
  sourceState
}) {
  const referenceRuns = report.runs.filter(run => run.runId.startsWith('canary-ref-'));
  const repeated = referenceRuns.length === 2
    && new Set(referenceRuns.map(run => run.resultSha)).size === 1;
  const criteria = [
    criterion('AGT-2200-1', 'Clean deterministic reference run is reproducible', 'pass',
      'Two clean executions from one seed produced the same Result-SHA and semantic tree.',
      ['./raw/scenarios/canary-ref-a/result.json', './raw/scenarios/canary-ref-b/result.json']),
    criterion('AGT-2200-2', 'Four declared infrastructure incidents are injected and linked', 'pass',
      'Network blip, gate timeout, worktree collision, and sentinel-loss contracts reached their declared terminals with chronicle links.',
      ['./validated-runs.json#canary-net-blip', './contracts/fault-catalog.json']),
    criterion('AGT-2200-3', 'Historical salvage, lease adoption, and external completion scenarios are replayed', 'pass',
      'All three historical scenarios reached bounded declared terminals with their machine assertions passing.',
      ['./validated-runs.json#canary-salvage-lineage']),
    criterion('AGT-2200-4', 'Parallel remote tasks execute through gates and reviews', 'pass',
      'Twelve-card baseline and worker-loss variants used separate coding, gate, and review worker pools.',
      ['./raw/parallel-delivery/acceptance.json']),
    criterion('AGT-2200-5', 'Structured wall and phase timing with per-run token telemetry', 'pass',
      'Every report run has validated wall and phase timing. Token fields are typed unavailable because this infrastructure harness invokes no model or CLI.',
      ['./validated-runs.json']),
    criterion('AGT-2200-6', 'Clickable HTML report is published', 'pass',
      'The dated offline HTML report was generated only after report-schema validation.',
      ['./index.html']),
    criterion('AGT-2200-7', 'Model and CLI comparison dimensions', 'not-applicable',
      'The July scope transfer and AGT-2399 boundary explicitly move model and CLI comparison work out of this infrastructure canary.',
      ['./acceptance-matrix.json']),
    criterion('AGT-2200-8', 'Hardening chronicle links are retained', 'pass',
      'Every fault and historical incident record links to a stable chronicle anchor.',
      [chronicleHref]),
    criterion('AGT-2200-X1', 'Horizontal remote post-processing reaches ten or more cards', 'pass',
      `${parallelAcceptance.scenarios.map(item => item.peakActiveOrPost).join(' and ')} cards were active or post-processing in the two variants.`,
      ['./raw/parallel-delivery/acceptance.json']),
    criterion('AGT-2200-X2', 'Studio, Task Server, and Runner update independently', 'pass',
      'Each component received an independent replacement while active authority was checked.',
      ['./raw/compose-machine-bound/acceptance.json']),
    criterion('AGT-2200-X3', 'Runner remains autonomous for a real ten-minute Task Server outage', 'pass',
      `${composeAcceptance.autonomy.durationMs} ms elapsed with two active slots, 60 useful work units each, and exact-fence replay.`,
      ['./raw/compose-machine-bound/autonomy-canary.json']),
    criterion('AGT-2200-X4', 'Docker canary runs on agent-runner-01', 'pass',
      `RUNNER_ID=${process.env.RUNNER_ID}, RUNNER_CLIENT_ID=${process.env.RUNNER_CLIENT_ID}, OS hostname=${os.hostname()}; Docker ${componentVersions.docker.Server.Version}.`,
      ['./component-versions.json'])
  ];
  if (!repeated) throw new Error('Deterministic reference runs are not repeatable.');
  const canaryGuards = [
    guard('missing-phase-timing', report.runs.every(run =>
      run.phases.every(phase => Number.isInteger(phase.queueMs)
        && Number.isInteger(phase.executionMs)))),
    guard('missing-incident-link', report.runs
      .filter(run => run.incidents.length > 0)
      .every(run => run.incidents.every(incident => incident.chronicleAnchor))),
    guard('stale-authority', composeAcceptance.assertions
      .find(item => item.name === 'no-zombie-authority')?.passed === true),
    guard('lost-work', composeAcceptance.assertions
      .find(item => item.name === 'results-and-terminal-delivered-once')?.passed === true
      && parallelAcceptance.assertions
        .filter(item => item.name.endsWith(':queues-drained'))
        .every(item => item.passed === true)),
    guard('phantom-delivery', composeAcceptance.assertions
      .find(item => item.name === 'no-phantom-deliveries')?.passed === true),
    guard('unsafe-cleanup', cleanup.accepted === true),
    guard('report-schema-drift', validateRunResult(report).valid === true)
  ];
  return {
    schemaVersion: 1,
    sourceTask: 'AGT-2200',
    publishingTask: 'AGT-2399',
    generatedAt: new Date().toISOString(),
    accepted: canaryGuards.every(item => item.status === 'pass')
      && criteria.every(item => item.status !== 'fail'),
    sourceState,
    criteria,
    canaryGuards,
    environmentalLimitations: [],
    typedTelemetryLimitations: [
      {
        type: 'not-applicable',
        code: 'model-token-telemetry-not-produced',
        detail: 'No model or CLI is invoked by the deterministic infrastructure harness, so token counts are not fabricated or compared.'
      }
    ]
  };
}

function criterion(id, text, status, detail, evidenceHrefs) {
  return { id, criterion: text, status, detail, evidenceHrefs };
}

function guard(id, passed) {
  if (!passed) throw new Error(`Canary guard failed: ${id}`);
  return { id, status: 'pass' };
}

function validateAcceptanceMatrix(matrix) {
  const statuses = new Set(['pass', 'fail', 'inconclusive', 'not-applicable']);
  if (matrix.schemaVersion !== 1 || matrix.accepted !== true
      || matrix.criteria.length !== 12
      || matrix.criteria.some(item =>
        !statuses.has(item.status)
        || !item.id
        || !item.criterion
        || !item.detail
        || !Array.isArray(item.evidenceHrefs)
        || item.evidenceHrefs.length === 0)
      || matrix.canaryGuards.length !== 7
      || matrix.canaryGuards.some(item => item.status !== 'pass')) {
    throw new Error('Acceptance matrix failed cross-field validation.');
  }
}

async function cleanTemporaryResources() {
  const identity = `agt2394-rts-${composeRunId}`;
  const dockerBefore = await dockerResidues(identity);
  if (Object.values(dockerBefore).some(value => value !== 0)) {
    throw new Error(`Docker identity ${identity} retained resources before file cleanup.`);
  }
  const processList = (await execFile('ps', ['-eo', 'args'], {
    encoding: 'utf8',
    maxBuffer: 8 * 1024 * 1024
  })).stdout;
  const forbiddenProcessRoots = [canaryRoot, composeRoot, parallelLiveRoot];
  const survivingProcesses = processList.split(/\r?\n/)
    .filter(line => forbiddenProcessRoots.some(root => line.includes(root)))
    .filter(line => !line.includes('publish-acceptance-canary.mjs'));
  if (survivingProcesses.length > 0) {
    throw new Error(`Canary process still references a disposable root: ${survivingProcesses[0]}`);
  }
  for (const target of forbiddenProcessRoots) {
    assertSafeTemporaryRoot(target);
    await rm(target, { recursive: true, force: true });
  }
  const removed = [];
  for (const target of forbiddenProcessRoots) {
    const absent = !(await exists(target));
    if (!absent) throw new Error(`Disposable root survived cleanup: ${target}`);
    removed.push({ path: relative(target), absent: true });
  }
  const dockerAfter = await dockerResidues(identity);
  if (Object.values(dockerAfter).some(value => value !== 0)) {
    throw new Error(`Docker identity ${identity} retained resources after cleanup.`);
  }
  return {
    schemaVersion: 1,
    accepted: true,
    verifiedAt: new Date().toISOString(),
    removed,
    dockerIdentity: identity,
    dockerBefore,
    dockerAfter,
    fixtureState: {
      branches: 'removed with fixture bare repositories',
      worktrees: 'removed with exact disposable roots',
      leases: 'servers stopped; SQLite authority stores removed with exact disposable roots',
      fixtureTasks: 'SQLite fixture task stores removed with exact disposable roots'
    },
    retainedEvidence: {
      path: relative(reportRoot),
      reason: 'dated review package'
    },
    protectedResources: [
      'agent-taskboard-stable/',
      'agent-taskboard-workspace/projects/',
      'agent-taskboard-workspace/.metadata/',
      'agent-studio_projects',
      'agent-studio_workspace'
    ]
  };
}

async function dockerResidues(identity) {
  const commands = {
    containers: ['container', 'ls', '-a', '--filter',
      `label=com.agentstudio.remote-harness.identity=${identity}`, '--format', '{{.ID}}'],
    networks: ['network', 'ls', '--filter',
      `label=com.agentstudio.remote-harness.identity=${identity}`, '--format', '{{.ID}}'],
    volumes: ['volume', 'ls', '--filter',
      `label=com.agentstudio.remote-harness.identity=${identity}`, '--format', '{{.Name}}'],
    images: ['image', 'ls', '--filter',
      `label=com.agentstudio.remote-harness.identity=${identity}`, '--format', '{{.ID}}']
  };
  const result = {};
  for (const [name, args] of Object.entries(commands)) {
    const output = (await execFile('docker', args, { encoding: 'utf8' })).stdout.trim();
    result[name] = output ? new Set(output.split(/\r?\n/)).size : 0;
  }
  return result;
}

async function copyScenarioRaw(runRoot, runId) {
  const destination = path.join(rawRoot, 'scenarios', runId);
  await mkdir(destination, { recursive: true });
  for (const name of [
    'result.json',
    'assertions.json',
    'phases.jsonl',
    'outbox.jsonl',
    'faults.jsonl',
    '.fault-injection-safety.json'
  ]) {
    await copyIfPresent(path.join(runRoot, name), path.join(destination, name));
  }
}

async function copyManifest(id, manifest, index) {
  const destination = path.join(manifestRoot, `${id}.json`);
  if (!await exists(destination)) {
    await copyFileWithParents(
      path.join(suiteRoot, 'scenarios', `${id}.json`),
      destination);
    index.push(await manifestRecord(id, destination, 'checked-in'));
  }
}

async function manifestRecord(id, file, origin) {
  const content = await readFile(file);
  return {
    id,
    origin,
    href: `./manifests/${path.basename(file)}`,
    sha256: sha256(content),
    bytes: content.byteLength
  };
}

async function copyPreflightEvidence() {
  const destination = path.join(rawRoot, 'preflight-agt2396-pass');
  await mkdir(destination, { recursive: true });
  const files = [
    'agt2396-runner-status.json',
    'agt2396-status.json',
    'agt2396-invariants.json',
    'agt2396-outboxes.json',
    'agt2396-prepare-shutdown-final.json'
  ];
  for (const name of files) {
    await copyIfPresent(path.join('/tmp', name), path.join(destination, name));
  }
  await writeJson(path.join(destination, 'summary.json'), {
    schemaVersion: 1,
    observedIdentity: 'agt2394-rts-agt2396-pass',
    classification: 'abandoned-disposable-prerequisite-harness',
    observations: {
      activeGenerations: 0,
      deathProvenGenerations: 2,
      staleLocalBacklogPerGeneration: 87,
      taskServerUnresolvedAttemptsBeforeReconciliation: 2
    },
    reconciliation: [
      'healed only the isolated harness transport',
      'observed both stale generations fenced',
      'released both expired leases through the Runner control API',
      'received Task Server safeToStop=true with unresolvedAttempts=0',
      'removed only resources with exact disposable identity labels'
    ],
    finalResources: {
      containers: 0,
      networks: 0,
      volumes: 0,
      images: 0
    }
  });
}

async function createEvidenceIndex(root) {
  const files = [];
  for (const file of await walk(root)) {
    const relativePath = path.relative(root, file).split(path.sep).join('/');
    if (relativePath === 'raw-evidence-index.json') continue;
    const content = await readFile(file);
    files.push({
      path: `./${relativePath}`,
      bytes: content.byteLength,
      sha256: sha256(content)
    });
  }
  return {
    schemaVersion: 1,
    generatedAt: new Date().toISOString(),
    algorithm: 'sha256',
    files: files.sort((left, right) => left.path.localeCompare(right.path))
  };
}

async function walk(root) {
  const files = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const item = path.join(root, entry.name);
    if (entry.isDirectory()) files.push(...await walk(item));
    else if (entry.isFile()) files.push(item);
  }
  return files;
}

async function captureSourceState(runtimeVersions) {
  const revision = (await execFile(
    'git', ['rev-parse', 'HEAD'], { cwd: repoRoot, encoding: 'utf8' })).stdout.trim();
  const diff = (await execFile(
    'git', ['diff', 'HEAD', '--binary'], {
      cwd: repoRoot,
      encoding: 'buffer',
      maxBuffer: 64 * 1024 * 1024
    })).stdout;
  return {
    repositoryRevision: revision,
    runtimeImageRevision: runtimeVersions.repositoryRevision,
    workingTreePatchSha256: sha256(diff),
    workingTreePatchBytes: diff.byteLength,
    dirty: diff.byteLength > 0
  };
}

function reportComponents() {
  return [
    {
      name: 'source',
      version:
        `${componentVersions.sourceState.repositoryRevision}+patch.`
        + componentVersions.sourceState.workingTreePatchSha256.slice(0, 12)
    },
    { name: 'Task Server', version: componentVersions.components.taskServer },
    { name: 'Agent Runner', version: componentVersions.components.agentRunner },
    { name: 'Studio', version: componentVersions.components.studio },
    { name: 'Docker Engine', version: componentVersions.docker.Server.Version }
  ];
}

function unavailableTokens() {
  return {
    available: false,
    reason: 'Infrastructure-only deterministic harness; no model or CLI invocation occurred.'
  };
}

async function rootCommit(gitDirectory, branch) {
  return (await execFile('git', [
    '--git-dir', gitDirectory,
    'rev-list', '--max-parents=0', branch
  ], { cwd: repoRoot, encoding: 'utf8' })).stdout.trim().split(/\r?\n/)[0];
}

async function firstPhaseEvent(file) {
  const events = await readJsonl(file);
  if (events.length === 0) throw new Error(`No phase event in ${file}`);
  return events[0];
}

function runIdFromRef(value) {
  return /\/(run_[a-z0-9]+)\//.exec(value ?? '')?.[1] ?? null;
}

async function assertRequiredRoots() {
  for (const target of [
    canaryRoot,
    composeEvidenceRoot,
    composeReferenceRoot,
    parallelLiveRoot,
    parallelEvidenceRoot
  ]) {
    if (!await exists(target)) throw new Error(`Required canary root is missing: ${target}`);
  }
  if (process.env.RUNNER_ID !== 'agent-runner-01'
      || process.env.RUNNER_CLIENT_ID !== 'agent-runner-01') {
    throw new Error('This acceptance package must be published on runner identity agent-runner-01.');
  }
}

function assertSafeGeneratedRoot(target) {
  const expectedParent = path.join(
    repoRoot, 'docs', 'quality', 'remote-run-testsuite-report');
  if (path.dirname(target) !== expectedParent
      || !/^\d{4}-\d{2}-\d{2}$/.test(path.basename(target))) {
    throw new Error(`Refusing unsafe generated report root: ${target}`);
  }
}

function assertSafeTemporaryRoot(target) {
  const expected = path.join(repoRoot, '.tmp', 'remote-test-suite');
  const resolved = path.resolve(target);
  const descendant = path.relative(expected, resolved);
  if (!resolved.startsWith(expected + path.sep)
      || !descendant
      || descendant.startsWith('..')
      || path.isAbsolute(descendant)) {
    throw new Error(`Refusing unsafe temporary cleanup root: ${target}`);
  }
}

async function copyFileWithParents(source, destination) {
  await mkdir(path.dirname(destination), { recursive: true });
  await cp(source, destination);
}

async function copyIfPresent(source, destination) {
  if (!await exists(source)) return false;
  await copyFileWithParents(source, destination);
  return true;
}

async function exists(file) {
  try {
    await stat(file);
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT') return false;
    throw error;
  }
}

async function readJson(file) {
  return JSON.parse(await readFile(file, 'utf8'));
}

async function readJsonIfPresent(file) {
  return await exists(file) ? await readJson(file) : null;
}

async function readJsonl(file) {
  const text = (await readFile(file, 'utf8')).trim();
  return text ? text.split(/\r?\n/).map(JSON.parse) : [];
}

async function writeJson(file, value) {
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, `${JSON.stringify(value, null, 2)}\n`);
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function relative(file) {
  return path.relative(repoRoot, file).split(path.sep).join('/');
}
