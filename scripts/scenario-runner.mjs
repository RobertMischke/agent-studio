#!/usr/bin/env node
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { performance } from 'node:perf_hooks';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, '..');
const definitionPath = path.join(repositoryRoot, 'testsupport', 'scenario', 'deployment-scenario.json');
const definition = JSON.parse(await readFile(definitionPath, 'utf8'));
const target = process.env.SCENARIO_TARGET ?? 'inproc';
const level = process.env.SCENARIO_LEVEL ?? 'smoke';
const runIndex = Number.parseInt(process.env.SCENARIO_RUN_INDEX ?? '1', 10);
const baseUrl = (process.env.SCENARIO_URL ?? '').replace(/\/$/, '');
const resultsRoot = path.resolve(process.env.SCENARIO_RESULTS_DIR ?? process.env.JOB_RESULTS_DIR ?? path.join(repositoryRoot, 'results'));
const reportStem = `deployment-scenario-${target}-${level}-${runIndex}`;
const reportDirectory = path.join(resultsRoot, reportStem);
const fixedStart = new Date(definition.clock.startedAtUtc);
const selectedSteps = definition.steps.filter(step => level === 'full' || step.level === 'smoke');
const allowedAssertions = new Set([
  'audit.actor-set', 'runner.status', 'task.state', 'claim.status',
  'fixture.tests', 'review.outcome', 'orchestration.status', 'chat.receipt',
  'dossier.decision', 'backup.verified', 'restore.verified', 'inventory.sha256'
]);

if (!['inproc', 'compose', 'remote'].includes(target) || !['smoke', 'full'].includes(level) || !baseUrl) {
  console.error('Scenario configuration is invalid: target, level, and URL are required.');
  process.exit(2);
}
assert.equal(definition.schemaVersion, 1);
assert.equal(new Set(definition.steps.map(step => step.id)).size, definition.steps.length);
assert.deepEqual(definition.steps.slice(0, 6).map(step => step.level), Array(6).fill('smoke'));
for (const step of definition.steps) assert.ok(allowedAssertions.has(step.assertion.type), `Unknown typed assertion ${step.assertion.type}`);

await mkdir(reportDirectory, { recursive: true });
const temporaryRoot = await mkdtemp(path.join(os.tmpdir(), 'agent-studio-deployment-scenario-'));
const nonce = `${process.pid}-${runIndex}-${Date.now().toString(36)}`;
const ids = {
  workspace: `ws_deployment_${nonce}`,
  project: `prj_deployment_${nonce}`,
  dossier: `task_dossier_${nonce}`,
  epic: `task_epic_${nonce}`,
  epicOne: `task_epic_one_${nonce}`,
  epicTwo: `task_epic_two_${nonce}`,
  task: `task_run_${nonce}`,
  runner: `scenario-coding-${nonce}`,
  runnerInstance: `scenario-coding-instance-${nonce}`,
  reviewer: `scenario-review-${nonce}`,
  reviewerInstance: `scenario-review-instance-${nonce}`
};
const keySuffix = crypto.createHash('sha256').update(nonce).digest('hex').slice(0, 8).toUpperCase();
const taskKeys = {
  dossier: `DSR-${keySuffix}D`,
  epic: `DSR-${keySuffix}E`,
  epicOne: `DSR-${keySuffix}1`,
  epicTwo: `DSR-${keySuffix}2`,
  run: `DSR-${keySuffix}R`
};
const state = { createdTasks: [], sequence: 0 };
const records = [];
let failed = false;

const sha256 = value => crypto.createHash('sha256').update(value).digest('hex');
const json = value => JSON.stringify(value, null, 2) + '\n';
const fixedTime = index => new Date(fixedStart.getTime() + index * definition.clock.stepOffsetMilliseconds).toISOString();
const headers = actor => ({
  'content-type': 'application/json',
  'x-task-server-protocol': '2',
  'x-client-version': 'deployment-scenario-v1',
  'x-actor-id': actor,
  ...(process.env.SCENARIO_BEARER_TOKEN ? { authorization: `Bearer ${process.env.SCENARIO_BEARER_TOKEN}` } : {})
});

async function request(method, route, body, actor = 'scenario-card') {
  const response = await fetch(`${baseUrl}${route}`, {
    method,
    headers: headers(actor),
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(30_000)
  });
  const text = await response.text();
  let payload = null;
  if (text) {
    try { payload = JSON.parse(text); } catch { payload = text; }
  }
  if (!response.ok) throw new Error(`${method} ${route} returned ${response.status}: ${text}`);
  return payload;
}

function command(file, args, options = {}) {
  const result = spawnSync(file, args, { encoding: 'utf8', ...options });
  return { exitCode: result.status ?? 1, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

async function setUpRepository() {
  const repository = path.join(temporaryRoot, 'repository');
  await cp(path.join(repositoryRoot, 'testsupport', 'scenario', definition.fixture.repository), repository, { recursive: true });
  let result = command('git', ['init', '-b', 'main'], { cwd: repository });
  assert.equal(result.exitCode, 0, result.stderr);
  result = command('git', ['add', '.'], { cwd: repository });
  assert.equal(result.exitCode, 0, result.stderr);
  result = command('git', ['-c', 'user.name=Deployment Scenario', '-c', 'user.email=scenario@example.invalid',
    'commit', '-m', 'test: seed deployment scenario'], {
    cwd: repository,
    env: { ...process.env, GIT_AUTHOR_DATE: fixedTime(0), GIT_COMMITTER_DATE: fixedTime(0) }
  });
  assert.equal(result.exitCode, 0, result.stderr);
  state.repository = repository;
  state.baseSha = command('git', ['rev-parse', 'HEAD'], { cwd: repository }).stdout.trim();
  result = command('git', ['checkout', '-b', 'scenario-result'], { cwd: repository });
  assert.equal(result.exitCode, 0, result.stderr);
  return repository;
}

async function createTask(taskId, taskKey, title, body, taskState = '0-backlog') {
  const task = await request('POST', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks`, {
    taskId, taskKey, title, body, state: taskState
  });
  state.createdTasks.push(task);
  return task;
}

async function inventory() {
  const [project, tasks, contexts] = await Promise.all([
    request('GET', `/api/v1/projects?workspaceId=${encodeURIComponent(ids.workspace)}`),
    request('GET', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks`),
    request('GET', '/api/v1/orchestrator-contexts?includeHidden=true')
  ]);
  const normalized = {
    project: project.map(item => ({ projectId: item.projectId, workspaceId: item.workspaceId, name: item.name, taskKeyPrefix: item.taskKeyPrefix })),
    tasks: tasks.map(item => ({ taskId: item.taskId, taskKey: item.taskKey, title: item.title, state: item.state, body: item.body })).sort((a, b) => a.taskId.localeCompare(b.taskId)),
    contexts: contexts.contexts.filter(item => item.projectId === ids.project).map(item => ({ contextKey: item.contextKey, kind: item.kind, turnCount: item.turnCount, summary: item.summary })).sort((a, b) => a.contextKey.localeCompare(b.contextKey))
  };
  return { normalized, sha256: sha256(JSON.stringify(normalized)) };
}

const actions = {
  async 'bootstrap-principals'() {
    await request('GET', '/api/v1/protocol', undefined, 'scenario-operator');
    const workspace = await request('POST', '/api/v1/workspaces', {
      workspaceId: ids.workspace, name: definition.fixture.workspaceName
    }, 'scenario-operator');
    const project = await request('POST', '/api/v1/projects', {
      projectId: ids.project, workspaceId: workspace.workspaceId,
      name: `${definition.fixture.projectName} ${keySuffix}`,
      taskKeyPrefix: `${definition.fixture.taskKeyPrefix}${keySuffix.slice(0, 6)}`
    }, 'scenario-operator');
    state.project = project;
    state.dossier = await createTask(ids.dossier, taskKeys.dossier, definition.fixture.dossierTitle,
      'Type: dossier\nDecision gate: pending\nClock: 2026-09-06T12:00:00Z');
    state.epic = await createTask(ids.epic, taskKeys.epic, definition.fixture.epicTitle,
      'Type: epic\nChildren: ' + [taskKeys.epicOne, taskKeys.epicTwo].join(', '));
    state.epicOne = await createTask(ids.epicOne, taskKeys.epicOne, definition.fixture.epicTasks[0], `Parent epic: ${taskKeys.epic}`);
    state.epicTwo = await createTask(ids.epicTwo, taskKeys.epicTwo, definition.fixture.epicTasks[1], `Parent epic: ${taskKeys.epic}`);
    if (target === 'compose') {
      await request('PUT', '/api/v1/hosts/scenario-compose-host/project-policy', {
        allowAllProjects: false, allowedProjectIds: [], expectedVersion: 0
      }, 'scenario-operator');
    }
    const audit = await request('GET', '/api/v1/management/audit', undefined, 'scenario-operator');
    const actors = new Set(audit.map(item => item.actorId));
    for (const expected of ['scenario-operator', 'scenario-card']) assert.ok(actors.has(expected));
    return { workspaceId: workspace.workspaceId, projectId: project.projectId, actors: [...actors].sort(), dossier: taskKeys.dossier, epic: taskKeys.epic, epicTasks: [taskKeys.epicOne, taskKeys.epicTwo] };
  },

  async 'register-runner'() {
    const runner = await request('PUT', `/api/v1/runners/${encodeURIComponent(ids.runner)}`, {
      name: ids.runner, hostId: 'scenario-host', instanceId: ids.runnerInstance,
      runnerVersion: 'scenario-1.0.0', protocolVersion: 2,
      capabilities: ['coding-executor']
    }, ids.runner);
    assert.equal(runner.status, 'active');
    return { runnerId: runner.runnerId, status: runner.status, hostId: runner.hostId };
  },

  async 'create-task'() {
    state.task = await createTask(ids.task, taskKeys.run, 'Deterministic deployment run',
      'Run the seeded failing test, fix it with the fake CLI, and retain the evidence.', '2-ready');
    assert.equal(state.task.state, '2-ready');
    return { taskId: state.task.taskId, taskKey: state.task.taskKey, state: state.task.state };
  },

  async claim() {
    state.claim = await request('POST', `/api/v1/runners/${encodeURIComponent(ids.runner)}/claims`, {
      runnerId: ids.runner, instanceId: ids.runnerInstance, requestedTtlSeconds: 120, availableSlots: 1
    }, ids.runner);
    assert.equal(state.claim.status, 'claimed');
    assert.equal(state.claim.task.taskId, ids.task);
    return { status: state.claim.status, runId: state.claim.run.runId, leaseId: state.claim.lease.leaseId, fence: state.claim.lease.fence };
  },

  async 'run-fake-cli'() {
    const repository = await setUpRepository();
    const baseline = command(process.execPath, ['--test', 'scenario.test.mjs'], { cwd: repository });
    assert.notEqual(baseline.exitCode, 0, 'The seeded repository must start with one failing test.');
    const cliLog = path.join(reportDirectory, 'fake-coding-cli.log');
    await writeFile(cliLog, '', 'utf8');
    const fake = command(process.execPath, [path.join(repositoryRoot, 'testsupport', 'scenario', 'fake-coding-cli.mjs'), repository, cliLog], { cwd: repository });
    assert.equal(fake.exitCode, 0, fake.stderr);
    assert.match(fake.stdout, /\[\[TASK_DONE\]\]/);
    const passing = command(process.execPath, ['--test', 'scenario.test.mjs'], { cwd: repository });
    assert.equal(passing.exitCode, 0, passing.stderr || passing.stdout);
    state.resultSha = command('git', ['rev-parse', 'HEAD'], { cwd: repository }).stdout.trim();
    state.treeSha = command('git', ['rev-parse', 'HEAD^{tree}'], { cwd: repository }).stdout.trim();
    assert.notEqual(state.resultSha, state.baseSha);
    const log = await readFile(cliLog);
    const logSha = sha256(log);
    const runId = state.claim.run.runId;
    const lease = state.claim.lease;
    await request('POST', `/api/v1/runs/${encodeURIComponent(runId)}/events`, {
      eventId: `evt-${nonce}`, kind: 'agent.message',
      payloadJson: JSON.stringify({ text: 'fixed fake CLI output', clock: fixedTime(4) }),
      idempotencyKey: `event-${nonce}`, fence: lease.fence,
      runnerId: ids.runner, instanceId: ids.runnerInstance, leaseId: lease.leaseId,
      sequence: ++state.sequence, occurredAt: fixedTime(4)
    }, ids.runner);
    await request('POST', `/api/v1/runs/${encodeURIComponent(runId)}/artifacts`, {
      artifactId: `artifact-${nonce}`, name: 'results/fake-coding-cli.log', mediaType: 'text/plain',
      contentBase64: log.toString('base64'), sha256: logSha, idempotencyKey: `artifact-${nonce}`,
      fence: lease.fence, runnerId: ids.runner, instanceId: ids.runnerInstance,
      leaseId: lease.leaseId, sequence: ++state.sequence
    }, ids.runner);
    const immutableRef = `refs/heads/agent-studio/results/${runId}/fence-${lease.fence}/${state.resultSha}`;
    const envelope = {
      repositoryId: `repo-${keySuffix.toLowerCase()}`, sourceRunAttemptId: runId,
      baseSha: state.baseSha, resultSha: state.resultSha, immutableRemoteRef: immutableRef,
      sourceBundleDigest: null, artifactManifestDigest: logSha, submodules: [], lfsObjects: [],
      repositoryUrl: pathToFileURL(repository).href
    };
    const canonicalEnvelope = { ...envelope, repositoryUrl: null };
    const envelopeDigest = sha256(JSON.stringify(canonicalEnvelope));
    await request('PUT', `/api/v1/runs/${encodeURIComponent(runId)}/result-handoff`, {
      runnerId: ids.runner, instanceId: ids.runnerInstance, leaseId: lease.leaseId,
      fence: lease.fence, sequence: ++state.sequence, idempotencyKey: `handoff-${nonce}`,
      envelopeDigest, envelope
    }, ids.runner);
    await request('POST', `/api/v1/runs/${encodeURIComponent(runId)}/completion`, {
      runnerId: ids.runner, instanceId: ids.runnerInstance, leaseId: lease.leaseId,
      fence: lease.fence, outcome: 'success', summary: 'The deterministic fixture now passes.',
      resultEnvelopeDigest: envelopeDigest, idempotencyKey: `completion-${nonce}`,
      sequence: ++state.sequence
    }, ids.runner);
    state.envelope = envelope;
    return { baselineExitCode: baseline.exitCode, passingExitCode: passing.exitCode, baseSha: state.baseSha, resultSha: state.resultSha, commitCreated: true, log: 'fake-coding-cli.log' };
  },

  async 'auto-review'() {
    const plan = { commands: [{ stepId: 'fake-review', aspect: 'build-tests', fileName: 'node', arguments: ['--test', 'scenario.test.mjs'], required: true, timeoutSeconds: 30, compareToBaseline: false, executionKind: 'tool' }], requiredAspects: ['build-tests'], requiresVisualReview: false, requireDifferentHostFailureDomain: false };
    const subject = await request('POST', '/api/v1/reviews/subjects', {
      taskId: ids.task, sourceRunId: state.claim.run.runId,
      repositoryId: state.envelope.repositoryId, repositoryUrl: state.envelope.repositoryUrl,
      expectedResultSha: state.resultSha, resultRef: state.envelope.immutableRemoteRef,
      sourceBundleArtifactId: null, sourceBundleSha256: null, codingHostId: 'scenario-host',
      reviewPolicyHash: sha256('deployment-scenario-review-v1'), plan,
      idempotencyKey: `review-subject-${nonce}`
    }, 'scenario-orchestrator');
    await request('PUT', `/api/v1/runners/${encodeURIComponent(ids.reviewer)}`, {
      name: ids.reviewer, hostId: 'scenario-review-host', instanceId: ids.reviewerInstance,
      runnerVersion: 'scenario-1.0.0', protocolVersion: 2,
      capabilities: ['review-executor', 'review:git']
    }, ids.reviewer);
    const claim = await request('POST', `/api/v1/runners/${encodeURIComponent(ids.reviewer)}/review-claims`, {
      executorId: ids.reviewer, instanceId: ids.reviewerInstance, requestedTtlSeconds: 120, availableSlots: 1
    }, ids.reviewer);
    assert.equal(claim.status, 'claimed');
    const reviewLogPath = path.join(reportDirectory, 'fake-review-cli.log');
    await writeFile(reviewLogPath, '', 'utf8');
    const fake = command(process.execPath, [path.join(repositoryRoot, 'testsupport', 'scenario', 'fake-review-cli.mjs'), state.repository, reviewLogPath], { cwd: state.repository });
    assert.equal(fake.exitCode, 0, fake.stderr || fake.stdout);
    const stdoutSha = sha256(fake.stdout);
    const stderrSha = sha256(fake.stderr);
    const lease = claim.lease;
    const workspace = `/review/${lease.resourceNamespace}`;
    const started = fixedTime(5);
    const finished = fixedTime(6);
    const report = await request('POST', `/api/v1/reviews/attempts/${encodeURIComponent(claim.attempt.attemptId)}/report`, {
      executorId: ids.reviewer, instanceId: ids.reviewerInstance, leaseId: lease.leaseId,
      fence: lease.fence, idempotencyKey: `review-report-${nonce}`, outcome: 'Pass',
      failureClassification: null, summary: 'The fake review CLI observed the passing fixture.',
      workspace: { repositoryId: state.envelope.repositoryId, expectedResultSha: state.resultSha,
        actualHead: state.resultSha, treeHash: state.treeSha, dirtyBefore: false, dirtyAfter: false,
        workspaceIdentity: sha256(workspace), resourceNamespace: lease.resourceNamespace },
      environment: { hostId: lease.hostId, executorId: ids.reviewer, instanceId: ids.reviewerInstance,
        osDescription: process.platform, architecture: process.arch, runtimeVersion: process.version,
        toolchain: { runtime: process.version, git: 'git;scenario-fixed', 'command:fake-review': `${process.execPath};scenario-fixed` },
        isolation: { workspace, cache: `${workspace}/cache`, temp: `${workspace}/tmp`,
          containers: lease.resourceNamespace, databases: lease.resourceNamespace,
          ports: `${lease.portBase}-${lease.portBase + 7}`, credentials: 'review-read-only' } },
      commands: [{ stepId: 'fake-review', aspect: 'build-tests', fileName: 'node',
        arguments: ['--test', 'scenario.test.mjs'], expectedResultSha: state.resultSha,
        headBefore: state.resultSha, treeBefore: state.treeSha, startedAt: started, finishedAt: finished,
        exitCode: 0, signal: null, stdoutSha256: stdoutSha, stderrSha256: stderrSha,
        baselineSha: null, newFailures: null, preExistingFailures: null, baselineCacheHit: false,
        retryPerformed: false, flakyQuarantinedFailures: null, phase: 'verification', workspaceRole: 'candidate',
        dependencyCacheHit: false, dependencyCache: null, executionKind: 'tool', executionLocation: 'remote',
        executorId: ids.reviewer, hostId: lease.hostId, attemptId: claim.attempt.attemptId,
        inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 }],
      artifacts: [
        { name: 'fake-review.stdout.log', mediaType: 'text/plain', sha256: stdoutSha, sizeBytes: Buffer.byteLength(fake.stdout), contentBase64: Buffer.from(fake.stdout).toString('base64') },
        { name: 'fake-review.stderr.log', mediaType: 'text/plain', sha256: stderrSha, sizeBytes: Buffer.byteLength(fake.stderr), contentBase64: Buffer.from(fake.stderr).toString('base64') }
      ],
      verdicts: [{ aspect: 'build-tests', status: 'pass', classification: 'Verified', summary: 'The deterministic tests pass.' }]
    }, ids.reviewer);
    assert.equal(report.outcome, 'Pass');
    const cleanup = await request('POST', `/api/v1/reviews/attempts/${encodeURIComponent(claim.attempt.attemptId)}/cleanup`, {
      executorId: ids.reviewer, instanceId: ids.reviewerInstance, leaseId: lease.leaseId,
      fence: lease.fence, idempotencyKey: `review-cleanup-${nonce}`, workspaceRemoved: true
    }, ids.reviewer);
    assert.equal(cleanup.status, 'cleaned');
    state.review = { subject, claim, report };
    return { subjectId: subject.subjectId, attemptId: claim.attempt.attemptId, outcome: report.outcome, cleanup: cleanup.status, log: 'fake-review-cli.log' };
  },

  async integrate() {
    let gitResult = command('git', ['checkout', 'main'], { cwd: state.repository });
    assert.equal(gitResult.exitCode, 0, gitResult.stderr);
    gitResult = command('git', ['merge', '--ff-only', state.resultSha], { cwd: state.repository });
    assert.equal(gitResult.exitCode, 0, gitResult.stderr);
    const integratedSha = command('git', ['rev-parse', 'main'], { cwd: state.repository }).stdout.trim();
    assert.equal(integratedSha, state.resultSha);
    let runs = await request('GET', `/api/v1/orchestration/runs?projectId=${encodeURIComponent(ids.project)}&status=pending`, undefined, 'scenario-engine');
    assert.equal(runs.length, 1);
    let run = runs[0];
    while (run.status === 'pending') {
      const claim = await request('POST', '/api/v1/orchestration/claims', {
        engineId: 'scenario-engine', instanceId: `scenario-engine-${nonce}`,
        supportedStages: [run.currentStage], requestedTtlSeconds: 120
      }, 'scenario-engine');
      const action = run.currentStage === 4 ? 3 : 0;
      run = await request('POST', `/api/v1/orchestration/runs/${encodeURIComponent(run.runId)}/stages/complete`, {
        engineId: 'scenario-engine', instanceId: `scenario-engine-${nonce}`,
        leaseId: claim.lease.leaseId, fence: claim.lease.fence, stage: run.currentStage,
        action, outputJson: JSON.stringify({ status: 'passed', clock: fixedTime(7) }),
        idempotencyKey: `orchestration-${nonce}-${run.currentStage}`
      }, 'scenario-engine');
    }
    assert.equal(run.status, 'completed');
    state.orchestration = run;
    return { runId: run.runId, status: run.status, integrationBranch: 'main', integratedSha,
      stages: run.stageResults.map(item => item.stage) };
  },

  async complete() {
    const task = await request('GET', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks/${encodeURIComponent(ids.task)}`);
    assert.equal(task.state, '5-human-review');
    const completed = await request('PUT', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks/${encodeURIComponent(ids.task)}`, {
      title: null, body: null, state: '6-completed', expectedVersion: task.version
    }, 'scenario-operator');
    assert.equal(completed.state, '6-completed');
    return { taskId: completed.taskId, state: completed.state, version: completed.version };
  },

  async 'orchestrator-chat'() {
    const route = `/api/v1/orchestrator-contexts/projects/${encodeURIComponent(state.project.name)}/turns`;
    const userTurnId = `scenario-user-${nonce}`;
    await request('POST', route, { turn: { turnId: userTurnId, createdAt: fixedTime(8), role: 'user', body: 'Summarize the deployment proof.' } }, 'scenario-operator');
    const sourceSha = sha256('deployment-scenario-context-v1');
    await request('POST', route, { turn: { turnId: `scenario-reply-${nonce}`, createdAt: fixedTime(9), role: 'orchestrator',
      body: 'The deterministic deployment proof completed.', model: 'scenario-fixed',
      receipt: { receiptId: `scenario-receipt-${nonce}`, userTurnId,
        contextKey: `project:${state.project.name}`, capturedAt: fixedTime(9),
        budget: { automaticSoftCapTokens: 4000, automaticHardCapTokens: 6000, totalHardCapTokens: 8000, estimatedIncludedTokens: 32 },
        sources: [{ sourceId: `project:${state.project.name}/scenario`, kind: 'project-base', revision: 'v1',
          sha256: sourceSha, freshness: 'current', includedCharacters: 128, estimatedTokens: 32, status: 'included' }] } } }, 'scenario-orchestrator');
    const transcript = await request('GET', route, undefined, 'scenario-operator');
    const reply = transcript.turns.find(turn => turn.receipt?.userTurnId === userTurnId);
    assert.ok(reply?.receipt);
    return { contextKey: transcript.context.contextKey, userTurnId, receiptId: reply.receipt.receiptId, sourceSha256: reply.receipt.sources[0].sha256 };
  },

  async 'dossier-decision'() {
    const dossier = await request('GET', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks/${encodeURIComponent(ids.dossier)}`);
    const decided = await request('PUT', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks/${encodeURIComponent(ids.dossier)}`, {
      title: null, body: 'Type: dossier\nDecision gate: approved\nDecision clock: 2026-09-06T12:00:10Z',
      state: '6-completed', expectedVersion: dossier.version
    }, 'scenario-operator');
    assert.match(decided.body, /Decision gate: approved/);
    return { dossierTaskKey: decided.taskKey, decision: 'approved', state: decided.state };
  },

  async backup() {
    state.inventoryBefore = await inventory();
    state.backup = await request('POST', '/api/v1/management/backups', { name: `deployment-${keySuffix.toLowerCase()}` }, 'scenario-operator');
    assert.equal(state.backup.sha256.length, 64);
    return { backupId: state.backup.backupId, sha256: state.backup.sha256, sizeBytes: state.backup.sizeBytes, inventorySha256: state.inventoryBefore.sha256 };
  },

  async 'restore-empty-store'() {
    const verifyOnly = target === 'remote' && process.env.SCENARIO_REMOTE_ALLOW_RESTORE !== '1';
    let transientTaskId = null;
    if (!verifyOnly) {
      transientTaskId = `task_restore_probe_${nonce}`;
      await createTask(transientTaskId, `DSR-${keySuffix}X`, 'Restore replacement probe',
        'This row must disappear when the backup replaces the target store.');
      await request('PUT', '/api/v1/management/mode', { mode: 3, reason: 'isolated deployment scenario restore' }, 'scenario-operator');
    }
    const restored = await request('POST', '/api/v1/management/restore', { backupId: state.backup.backupId, verifyOnly }, 'scenario-operator');
    assert.equal(restored.verified, true);
    assert.equal(restored.restored, !verifyOnly);
    if (!verifyOnly) {
      await request('PUT', '/api/v1/management/mode', { mode: 0, reason: 'deployment scenario restore completed' }, 'scenario-operator');
      const probe = await fetch(`${baseUrl}/api/v1/projects/${encodeURIComponent(ids.project)}/tasks/${encodeURIComponent(transientTaskId)}`, {
        headers: headers('scenario-operator'), signal: AbortSignal.timeout(30_000)
      });
      assert.equal(probe.status, 404, 'The pre-restore target row must not survive snapshot replacement.');
    }
    state.restore = restored;
    return { backupId: restored.backupId, verified: restored.verified, restored: restored.restored,
      replacementProbeRemoved: verifyOnly ? null : true,
      mode: verifyOnly ? 'isolated-verification' : 'isolated-store-replacement' };
  },

  async 'inventory-hash-equality'() {
    state.inventoryAfter = await inventory();
    assert.equal(state.inventoryAfter.sha256, state.inventoryBefore.sha256);
    return { before: state.inventoryBefore.sha256, after: state.inventoryAfter.sha256, equal: true, inventory: state.inventoryAfter.normalized };
  }
};

for (let index = 0; index < selectedSteps.length; index++) {
  const step = selectedSteps[index];
  const started = performance.now();
  let status = 'passed';
  let output;
  let error;
  if (failed) {
    status = 'skipped';
    error = 'A previous scenario step failed.';
  } else {
    try {
      output = await actions[step.id]();
    } catch (caught) {
      status = 'failed';
      failed = true;
      error = caught?.stack ?? String(caught);
    }
  }
  const durationMs = Math.max(0, Math.round((performance.now() - started) * 1000) / 1000);
  const evidence = { schemaVersion: 1, scenarioId: definition.id, target, level,
    stepId: step.id, status, durationMs, scenarioClock: fixedTime(index),
    assertion: step.assertion, output: output ?? null, error: error ?? null };
  const evidencePath = path.join(reportDirectory, step.evidence);
  await writeFile(evidencePath, json(evidence), 'utf8');
  records.push({ step, status, durationMs, evidencePath, error });
  console.log(`[scenario] ${step.id}: ${status} (${durationMs} ms)`);
}

const selectedDurationMs = records.reduce((sum, record) => sum + record.durationMs, 0);
if (level === 'smoke' && selectedDurationMs > definition.smokeBudgetSeconds * 1000) {
  failed = true;
  const last = records.at(-1);
  last.status = 'failed';
  last.error = `Smoke duration ${selectedDurationMs} ms exceeded ${definition.smokeBudgetSeconds * 1000} ms.`;
  const evidence = JSON.parse(await readFile(last.evidencePath, 'utf8'));
  evidence.status = 'failed';
  evidence.error = last.error;
  await writeFile(last.evidencePath, json(evidence), 'utf8');
}

if (target === 'remote' && state.createdTasks.length > 0) {
  for (const original of state.createdTasks) {
    try {
      const current = await request('GET', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks/${encodeURIComponent(original.taskId)}`);
      if (current.state !== '7-archive') {
        await request('PUT', `/api/v1/projects/${encodeURIComponent(ids.project)}/tasks/${encodeURIComponent(original.taskId)}`, {
          title: null, body: null, state: '7-archive', expectedVersion: current.version
        }, 'scenario-cleanup');
      }
    } catch (cleanupError) {
      failed = true;
      records.push({ step: { id: 'remote-cleanup', evidence: 'remote-cleanup.json', assertion: { type: 'cleanup.archive', expected: true } },
        status: 'failed', durationMs: 0, evidencePath: path.join(reportDirectory, 'remote-cleanup.json'), error: cleanupError?.stack ?? String(cleanupError) });
      await writeFile(path.join(reportDirectory, 'remote-cleanup.json'), json({ status: 'failed', error: cleanupError?.stack ?? String(cleanupError) }), 'utf8');
      break;
    }
  }
}

const escapeXml = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const failures = records.filter(record => record.status === 'failed').length;
const skipped = records.filter(record => record.status === 'skipped').length;
const totalSeconds = records.reduce((sum, record) => sum + record.durationMs, 0) / 1000;
const junit = `<?xml version="1.0" encoding="utf-8"?>\n<testsuite name="${escapeXml(definition.id)}" tests="${records.length}" failures="${failures}" skipped="${skipped}" time="${totalSeconds.toFixed(3)}">\n${records.map(record => {
  const detail = record.status === 'failed' ? `\n    <failure message="Scenario step failed">${escapeXml(record.error)}</failure>` : record.status === 'skipped' ? '\n    <skipped />' : '';
  return `  <testcase classname="deployment.${escapeXml(target)}.${escapeXml(level)}" name="${escapeXml(record.step.id)}" time="${(record.durationMs / 1000).toFixed(3)}">${detail}\n  </testcase>`;
}).join('\n')}\n</testsuite>\n`;
await writeFile(path.join(resultsRoot, `${reportStem}.junit.xml`), junit, 'utf8');
const markdown = `# Deployment scenario report\n\n- Scenario: \`${definition.id}\`\n- Target: \`${target}\`\n- Level: \`${level}\`\n- Result: **${failed ? 'FAILED' : 'PASSED'}**\n- Duration: ${(totalSeconds).toFixed(3)} s\n- Definition: \`testsupport/scenario/deployment-scenario.json\`\n\n| Step | Status | Duration | Evidence |\n|---|---:|---:|---|\n${records.map(record => `| \`${record.step.id}\` | ${record.status} | ${record.durationMs.toFixed(3)} ms | [${record.step.evidence}](${reportStem}/${record.step.evidence}) |`).join('\n')}\n`;
await writeFile(path.join(resultsRoot, `${reportStem}.md`), markdown, 'utf8');
await writeFile(path.join(resultsRoot, `${reportStem}.json`), json({ schemaVersion: 1, scenarioId: definition.id, target, level,
  status: failed ? 'failed' : 'passed', durationMs: totalSeconds * 1000,
  steps: records.map(record => ({ id: record.step.id, status: record.status, durationMs: record.durationMs, evidence: `${reportStem}/${record.step.evidence}` })) }), 'utf8');
await rm(temporaryRoot, { recursive: true, force: true });
process.exit(failed ? 1 : 0);
