#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import {
  appendFile,
  cp,
  mkdir,
  readFile,
  rm,
  stat,
  writeFile
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  cleanupRunRoot,
  resetRunRoot,
  runCommand,
  sha256
} from './core.mjs';
import {
  assertParallelEvidence,
  createParallelPlan,
  deterministicIntegrationOrder,
  parallelDefaults,
  summarizePressure,
  workerAssignments
} from './parallel-core.mjs';

class Api {
  constructor(baseUrl, actorId) {
    this.baseUrl = baseUrl.replace(/\/$/, '');
    this.actorId = actorId;
  }

  async get(route) {
    return await this.request('GET', route);
  }

  async post(route, body) {
    return await this.request('POST', route, body);
  }

  async put(route, body) {
    return await this.request('PUT', route, body);
  }

  async request(method, route, body) {
    const response = await fetch(`${this.baseUrl}${route}`, {
      method,
      headers: {
        'Content-Type': 'application/json',
        'X-Actor-Id': this.actorId,
        'X-Client-Id': this.actorId,
        'X-Task-Protocol-Version': '2',
        'X-Task-Client-Version': 'parallel-remote-test-suite/1'
      },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let value = null;
    try {
      value = text ? JSON.parse(text) : null;
    } catch {
      value = { raw: text };
    }
    if (!response.ok) {
      throw new Error(`${method} ${route} failed (${response.status}): ${text}`);
    }
    return value;
  }
}

class JsonlWriter {
  constructor(file) {
    this.file = file;
    this.pending = Promise.resolve();
  }

  append(value) {
    this.pending = this.pending.then(async () => {
      await mkdir(path.dirname(this.file), { recursive: true });
      await appendFile(this.file, `${JSON.stringify(value)}\n`);
    });
    return this.pending;
  }

  async flush() {
    await this.pending;
  }
}

const suiteRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(suiteRoot, '..', '..');
const args = parseArgs(process.argv.slice(2));
const baseRoot = path.resolve(args.root ?? path.join(repoRoot, '.tmp', 'remote-test-suite'));
const plan = createParallelPlan({
  baseRoot,
  runId: args.runId,
  options: {
    taskCount: args.taskCount,
    codingWorkers: args.codingWorkers,
    gateWorkers: args.gateWorkers,
    reviewWorkers: args.reviewWorkers,
    slotsPerWorker: args.slotsPerWorker
  }
});

if (args.dryRun) {
  console.log(JSON.stringify({ dryRun: true, ...plan }, null, 2));
  process.exit(0);
}

const port = await availablePort();
const serverUrl = `http://127.0.0.1:${port}`;
let taskServer;
let taskServerOutput = { value: '' };
let completed = false;
try {
  await resetRunRoot(plan.root, baseRoot);
  await mkdir(plan.evidenceRoot, { recursive: true });
  ({ child: taskServer, output: taskServerOutput } = await startTaskServer(plan.root, serverUrl));
  const scenarios = [];
  for (const name of plan.scenarios) {
    scenarios.push(await executeScenario({
      name,
      injectWorkerLoss: name === 'worker-loss',
      plan,
      serverUrl,
      seed: args.seed
    }));
  }
  const acceptance = {
    schemaVersion: 1,
    runId: args.runId,
    seed: args.seed,
    createdAt: new Date().toISOString(),
    scope: 'remote-infrastructure-only',
    modelComparisonDimensions: [],
    tokenTelemetryOnly: true,
    scenarios
  };
  acceptance.assertions = assertParallelEvidence(acceptance);
  acceptance.accepted = acceptance.assertions.every(item => item.passed);
  await writeFile(
    path.join(plan.evidenceRoot, 'acceptance.json'),
    `${JSON.stringify(acceptance, null, 2)}\n`
  );
  await writeFile(
    path.join(plan.evidenceRoot, 'concurrency-report.md'),
    renderReport(acceptance)
  );
  await writeFile(
    path.join(plan.evidenceRoot, 'resource-plan.json'),
    `${JSON.stringify({ ...plan, serverUrl }, null, 2)}\n`
  );
  completed = true;
  if (args.exportRoot) {
    const exportRoot = path.resolve(args.exportRoot);
    assertSafeExportRoot(exportRoot);
    await rm(exportRoot, { recursive: true, force: true });
    await mkdir(path.dirname(exportRoot), { recursive: true });
    await cp(plan.evidenceRoot, exportRoot, { recursive: true });
  }
  console.log(JSON.stringify({
    accepted: acceptance.accepted,
    runId: args.runId,
    evidenceRoot: plan.evidenceRoot,
    exportedEvidenceRoot: args.exportRoot ? path.resolve(args.exportRoot) : null,
    assertions: acceptance.assertions.length,
    scenarios: scenarios.map(scenario => ({
      name: scenario.name,
      tasks: scenario.tasks.length,
      peakActiveOrPost: scenario.peakActiveOrPost,
      codingWorkers: new Set(scenario.tasks.map(task => task.coding.workerId)).size,
      gateWorkers: new Set(scenario.tasks.map(task => task.gate.workerId)).size,
      reviewWorkers: new Set(scenario.tasks.map(task => task.review.workerId)).size,
      retries: scenario.workerLoss?.retries ?? 0,
      collisions: scenario.integrationCollisions
    }))
  }, null, 2));
} finally {
  taskServer?.kill('SIGTERM');
  await writeFile(
    path.join(plan.evidenceRoot, 'task-server.log'),
    redactTaskServerOutput(taskServerOutput.value)
  ).catch(() => {});
  if (completed && args.exportRoot) {
    await cp(plan.evidenceRoot, path.resolve(args.exportRoot), {
      recursive: true,
      force: true
    }).catch(() => {});
  }
  if (!completed && args.cleanupOnFailure) {
    await cleanupRunRoot(plan.root, baseRoot);
  }
}

async function executeScenario({ name, injectWorkerLoss, plan, serverUrl, seed }) {
  const scenarioRoot = path.join(plan.root, name);
  const evidenceRoot = path.join(plan.evidenceRoot, name);
  await mkdir(evidenceRoot, { recursive: true });
  const timeline = new JsonlWriter(path.join(evidenceRoot, 'timeline.jsonl'));
  const runtime = new JsonlWriter(path.join(evidenceRoot, 'runtime-events.jsonl'));
  const telemetry = new JsonlWriter(path.join(evidenceRoot, 'telemetry.jsonl'));
  const api = new Api(serverUrl, `parallel-harness:${plan.runId}:${name}`);
  const startedAt = new Date().toISOString();
  const occupancy = {
    coding: { active: 0, queued: plan.config.taskCount, capacity: plan.workerCapacity.coding },
    gate: { active: 0, queued: 0, capacity: plan.workerCapacity.gate },
    review: { active: 0, queued: 0, capacity: plan.workerCapacity.review },
    integration: { active: 0, queued: 0, capacity: 1 }
  };
  const samples = [];
  const sampler = startSampler(telemetry, occupancy, samples);
  const origin = await seedFixture(scenarioRoot, name, seed);
  const workspaceId = `wsp-${plan.runId}-${name}`;
  const projectId = `prj-${plan.runId}-${name}`;
  const taskKeyPrefix = name === 'baseline' ? 'RTB' : 'RTL';
  await api.post('/api/v1/workspaces', {
    name: `Parallel ${plan.runId} ${name}`,
    workspaceId
  });
  await api.post('/api/v1/projects', {
    workspaceId,
    name: `Parallel ${plan.runId} ${name}`,
    taskKeyPrefix,
    projectId
  });

  const tasks = [];
  for (let ordinal = 1; ordinal <= plan.config.taskCount; ordinal++) {
    const task = await api.post(`/api/v1/projects/${projectId}/tasks`, {
      title: `Isolated parallel reference delivery ${ordinal}`,
      body: `Create only the deterministic namespace for ${taskKeyPrefix}-${ordinal}. Infrastructure verification task.`,
      state: '2-ready',
      taskId: `tsk-${plan.runId}-${name}-${ordinal}`,
      taskKey: `${taskKeyPrefix}-${ordinal}`
    });
    tasks.push({
      ordinal,
      taskId: task.taskId,
      taskKey: task.taskKey,
      projectId,
      state: task.state
    });
  }

  const codingAssignments = qualifyAssignments(
    workerAssignments(
      plan.config.taskCount,
      'coding',
      plan.config.codingWorkers,
      plan.config.slotsPerWorker
    ),
    plan.runId,
    name
  );
  const gateAssignments = qualifyAssignments(
    workerAssignments(
      plan.config.taskCount,
      'gate',
      plan.config.gateWorkers,
      plan.config.slotsPerWorker
    ),
    plan.runId,
    name
  );
  const reviewAssignments = qualifyAssignments(
    workerAssignments(
      plan.config.taskCount,
      'review',
      plan.config.reviewWorkers,
      plan.config.slotsPerWorker
    ),
    plan.runId,
    name
  );
  const slotAdmissions = [];
  await registerWorkers(api, codingAssignments, 'coding-executor');
  await registerWorkers(api, gateAssignments, 'gate-executor');
  await registerWorkers(api, reviewAssignments, 'review-executor');

  for (const assignment of codingAssignments) {
    const task = tasks[assignment.ordinal - 1];
    const decisionAt = new Date().toISOString();
    const claim = await api.post(`/api/v1/runners/${assignment.workerId}/claims`, {
      runnerId: assignment.workerId,
      instanceId: assignment.instanceId,
      requestedTtlSeconds: 120,
      availableSlots: assignment.availableBefore,
      requiredCapabilities: []
    });
    if (claim.status !== 'claimed' || claim.task.taskId !== task.taskId) {
      throw new Error(`Coding admission drift for ${task.taskKey}: ${JSON.stringify(claim)}`);
    }
    task.claim = claim;
    task.coding = {
      workerId: assignment.workerId,
      hostId: assignment.hostId,
      slot: assignment.slot
    };
    const admission = {
      scenario: name,
      role: 'coding',
      taskKey: task.taskKey,
      workerId: assignment.workerId,
      slot: assignment.slot,
      availableBefore: assignment.availableBefore,
      activeAfter: assignment.activeAfter,
      poolCapacity: plan.workerCapacity.coding,
      decision: 'admitted',
      decidedAt: decisionAt
    };
    slotAdmissions.push(admission);
    await timeline.append({ event: 'slot-admission', ...admission });
    await runtime.append(runtimeEvent({
      timestamp: decisionAt,
      level: 'Info',
      event: 'coding.slot-admission.completed',
      subsystem: 'runner',
      operation: 'coding-slot-admission',
      correlationId: task.taskKey,
      project: name,
      jobId: task.taskId,
      runId: claim.run.runId,
      status: 'Ok',
      payload: {
        workerId: assignment.workerId,
        slot: assignment.slot,
        decision: 'admitted',
        availableBefore: assignment.availableBefore,
        poolCapacity: plan.workerCapacity.coding
      }
    }));
  }
  occupancy.coding.active = tasks.length;
  occupancy.coding.queued = 0;
  const activeSnapshot = await api.get(`/api/v1/projects/${projectId}/tasks`);
  let peakActiveOrPost = activeSnapshot.filter(task => task.state === '3-progress').length;
  await writeJson(path.join(evidenceRoot, 'active-snapshot.json'), activeSnapshot);
  await timeline.append({
    event: 'concurrency-snapshot',
    scenario: name,
    active: peakActiveOrPost,
    postProcessing: 0,
    taskCount: tasks.length,
    at: new Date().toISOString()
  });

  await prepareCodingWorktrees(scenarioRoot, origin, tasks, codingAssignments);
  const codingBatch = await executeBatch({
    role: 'coding',
    tasks,
    assignments: codingAssignments,
    timeline,
    runtime,
    occupancy,
    evidenceRoot,
    recordAdmissions: false,
    inputFor: (task, assignment) => ({
      role: 'coding',
      workerId: assignment.workerId,
      hostId: assignment.hostId,
      slot: assignment.slot,
      workspace: task.coding.workspace,
      branch: task.coding.branch,
      taskKey: task.taskKey,
      ordinal: task.ordinal,
      seed: `${seed}-${name}-${task.ordinal}`,
      commitDate: deterministicDate(`${seed}-${name}-${task.ordinal}`),
      runAttemptId: task.claim.run.runId
    })
  });
  occupancy.coding.active = 0;
  for (const result of codingBatch.results) {
    const task = tasks[result.ordinal - 1];
    Object.assign(task.coding, result);
    task.resultSha = result.resultSha;
    task.resultRef = result.resultRef;
    const subjects = (await runCommand(
      ['git', 'log', '--format=%s', `${result.baseSha}..${result.resultSha}`],
      { cwd: result.workspace }
    )).stdout.trim().split(/\r?\n/).filter(Boolean);
    const mentioned = subjects.flatMap(subject =>
      subject.match(/[A-Z][A-Z0-9-]*-[0-9]+/g) ?? []);
    task.commitTaskKey = mentioned.length === 1 ? mentioned[0] : null;
    task.crossTaskCommits = mentioned.filter(key => key !== task.taskKey).length;
  }

  await Promise.all(tasks.map(async task => {
    const repositoryUrl = pathToFileURL(origin).href;
    const repositoryId = repositoryIdentity(repositoryUrl);
    const envelope = {
      repositoryId,
      sourceRunAttemptId: task.claim.run.runId,
      baseSha: task.coding.baseSha,
      resultSha: task.resultSha,
      immutableRemoteRef: task.resultRef,
      sourceBundleDigest: null,
      artifactManifestDigest: sha256('[]'),
      submodules: [],
      lfsObjects: [],
      repositoryUrl
    };
    const envelopeDigest = sha256(JSON.stringify({ ...envelope, repositoryUrl: null }));
    const handoff = {
      runnerId: task.coding.workerId,
      instanceId: assignmentFor(codingAssignments, task.ordinal).instanceId,
      leaseId: task.claim.lease.leaseId,
      fence: task.claim.lease.fence,
      sequence: 1,
      idempotencyKey: `${plan.runId}:${name}:${task.taskKey}:handoff`,
      envelopeDigest,
      envelope
    };
    const firstHandoff = await api.put(`/api/v1/runs/${task.claim.run.runId}/result-handoff`, handoff);
    const replayHandoff = await api.put(`/api/v1/runs/${task.claim.run.runId}/result-handoff`, handoff);
    task.handoffReplay = firstHandoff.replay === false && replayHandoff.replay === true;
    const completion = {
      runnerId: task.coding.workerId,
      instanceId: assignmentFor(codingAssignments, task.ordinal).instanceId,
      leaseId: task.claim.lease.leaseId,
      fence: task.claim.lease.fence,
      outcome: 'Done',
      summary: `Isolated deterministic delivery for ${task.taskKey}.`,
      resultEnvelopeDigest: envelopeDigest,
      idempotencyKey: `${plan.runId}:${name}:${task.taskKey}:completion`,
      sequence: 2
    };
    const firstCompletion = await api.post(`/api/v1/runs/${task.claim.run.runId}/completion`, completion);
    const replayCompletion = await api.post(`/api/v1/runs/${task.claim.run.runId}/completion`, completion);
    const taskAfterReplay = await api.get(`/api/v1/projects/${projectId}/tasks/${task.taskId}`);
    task.completionReplay = firstCompletion.runId === replayCompletion.runId
      && taskAfterReplay.version === task.claim.task.version + 1;
    task.repositoryId = repositoryId;
    task.repositoryUrl = repositoryUrl;
    await timeline.append({
      event: 'result-delivered',
      scenario: name,
      taskKey: task.taskKey,
      runAttemptId: task.claim.run.runId,
      resultSha: task.resultSha,
      handoffReplay: task.handoffReplay,
      completionReplay: task.completionReplay,
      at: new Date().toISOString()
    });
  }));

  const postSnapshot = await api.get(`/api/v1/projects/${projectId}/tasks`);
  const postCount = postSnapshot.filter(task => task.state === '4-auto-review').length;
  peakActiveOrPost = Math.max(peakActiveOrPost, postCount);
  await writeJson(path.join(evidenceRoot, 'post-processing-snapshot.json'), postSnapshot);
  occupancy.gate.queued = tasks.length;
  const gateBatch = await executeBatch({
    role: 'gate',
    tasks,
    assignments: gateAssignments,
    timeline,
    runtime,
    occupancy,
    evidenceRoot,
    injectWorkerLoss,
    inputFor: (task, assignment, retryCount) => ({
      role: 'gate',
      workerId: assignment.workerId,
      hostId: assignment.hostId,
      slot: assignment.slot,
      origin,
      workspace: path.join(
        scenarioRoot,
        'gate',
        assignment.workerId,
        `${task.taskKey.toLowerCase()}-attempt-${retryCount + 1}`
      ),
      taskKey: task.taskKey,
      ordinal: task.ordinal,
      resultRef: task.resultRef,
      expectedResultSha: task.resultSha,
      holdMs: injectWorkerLoss ? 500 : 100
    })
  });
  occupancy.gate.active = 0;
  occupancy.gate.queued = 0;
  for (const result of gateBatch.results) {
    const task = tasks[result.ordinal - 1];
    task.gate = result;
    await rm(result.workspace, { recursive: true, force: true });
    task.gate.workspaceRemoved = !(await exists(result.workspace));
  }

  for (const task of deterministicIntegrationOrder(tasks)) {
    task.subject = await api.post('/api/v1/reviews/subjects', {
      taskId: task.taskId,
      sourceRunId: task.claim.run.runId,
      repositoryId: task.repositoryId,
      repositoryUrl: task.repositoryUrl,
      expectedResultSha: task.resultSha,
      resultRef: task.resultRef,
      sourceBundleArtifactId: null,
      sourceBundleSha256: null,
      codingHostId: task.coding.hostId,
      reviewPolicyHash: sha256('parallel-reference-review-policy-v1'),
      plan: {
        commands: [{
          stepId: 'semantic-acceptance',
          aspect: 'semantic',
          fileName: 'node',
          arguments: ['--test', 'test/*.test.mjs'],
          required: true,
          timeoutSeconds: 120,
          compareToBaseline: false
        }],
        requiredAspects: ['semantic'],
        requiresVisualReview: false,
        requireDifferentHostFailureDomain: false,
        integrationRef: 'develop'
      },
      idempotencyKey: `${plan.runId}:${name}:${task.taskKey}:review-subject`
    });
  }

  const reviewJobs = [];
  for (const assignment of reviewAssignments) {
    const claim = await api.post(`/api/v1/runners/${assignment.workerId}/review-claims`, {
      executorId: assignment.workerId,
      instanceId: assignment.instanceId,
      requestedTtlSeconds: 120,
      availableSlots: assignment.availableBefore,
      requiredCapabilities: []
    });
    if (claim.status !== 'claimed') {
      throw new Error(`Review admission failed: ${JSON.stringify(claim)}`);
    }
    const task = tasks.find(item => item.taskId === claim.subject.taskId);
    if (!task) throw new Error(`Review claim referenced unknown task ${claim.subject.taskId}`);
    task.reviewClaim = claim;
    task.reviewAssignment = assignment;
    task.review = {
      workerId: assignment.workerId,
      hostId: assignment.hostId,
      slot: assignment.slot
    };
    reviewJobs.push(task);
    const admission = {
      scenario: name,
      role: 'review',
      taskKey: task.taskKey,
      workerId: assignment.workerId,
      slot: assignment.slot,
      availableBefore: assignment.availableBefore,
      activeAfter: assignment.activeAfter,
      poolCapacity: plan.workerCapacity.review,
      decision: 'admitted',
      decidedAt: new Date().toISOString()
    };
    slotAdmissions.push(admission);
    await timeline.append({ event: 'slot-admission', ...admission });
    await runtime.append(runtimeEvent({
      timestamp: admission.decidedAt,
      level: 'Info',
      event: 'review.slot-admission.completed',
      subsystem: 'review',
      operation: 'review-slot-admission',
      correlationId: task.taskKey,
      project: name,
      jobId: task.taskId,
      runId: task.claim.run.runId,
      status: 'Ok',
      payload: {
        workerId: assignment.workerId,
        slot: assignment.slot,
        decision: 'admitted',
        availableBefore: assignment.availableBefore,
        poolCapacity: plan.workerCapacity.review
      }
    }));
  }
  occupancy.review.queued = reviewJobs.length;
  const reviewBatch = await executeBatch({
    role: 'review',
    tasks: reviewJobs,
    assignments: reviewJobs.map(task => task.reviewAssignment),
    timeline,
    runtime,
    occupancy,
    evidenceRoot,
    recordAdmissions: false,
    inputFor: (task, assignment) => ({
      role: 'review',
      workerId: assignment.workerId,
      hostId: assignment.hostId,
      slot: assignment.slot,
      origin,
      attemptRoot: path.join(
        scenarioRoot,
        'review',
        assignment.workerId,
        task.reviewClaim.lease.resourceNamespace
      ),
      workspace: path.join(
        scenarioRoot,
        'review',
        assignment.workerId,
        task.reviewClaim.lease.resourceNamespace,
        'repository'
      ),
      taskKey: task.taskKey,
      ordinal: task.ordinal,
      resultRef: task.resultRef,
      expectedResultSha: task.resultSha,
      holdMs: 100
    })
  });
  occupancy.review.active = 0;
  occupancy.review.queued = 0;

  await Promise.all(reviewBatch.results.map(async result => {
    const task = tasks[result.ordinal - 1];
    Object.assign(task.review, result);
    const claim = task.reviewClaim;
    const reportRequest = {
      executorId: task.review.workerId,
      instanceId: task.reviewAssignment.instanceId,
      leaseId: claim.lease.leaseId,
      fence: claim.lease.fence,
      authorityEpoch: claim.lease.authorityEpoch,
      idempotencyKey: `${plan.runId}:${name}:${task.taskKey}:review-report`,
      outcome: 'Pass',
      failureClassification: null,
      summary: `Exact Result SHA semantic acceptance passed for ${task.taskKey}.`,
      workspace: {
        repositoryId: task.repositoryId,
        expectedResultSha: task.resultSha,
        actualHead: result.actualHead,
        treeHash: result.treeSha,
        dirtyBefore: result.dirtyBefore,
        dirtyAfter: result.dirtyAfter,
        workspaceIdentity: sha256(path.resolve(result.workspace, '..')),
        resourceNamespace: claim.lease.resourceNamespace
      },
      environment: {
        hostId: task.review.hostId,
        executorId: task.review.workerId,
        instanceId: task.reviewAssignment.instanceId,
        osDescription: process.platform,
        architecture: process.arch,
        runtimeVersion: process.version,
        toolchain: {
          runtime: process.version,
          git: 'git-from-harness-path',
          'command:semantic-acceptance': process.execPath
        },
        isolation: {
          serviceRole: 'remote-review-executor',
          workspace: path.resolve(result.workspace, '..'),
          cache: path.resolve(result.workspace, '..', 'cache'),
          temp: path.resolve(result.workspace, '..', 'tmp'),
          ports: `${claim.lease.portBase}-${claim.lease.portBase + 7}`,
          containers: claim.lease.resourceNamespace,
          databases: claim.lease.resourceNamespace,
          credentials: 'review-read-only'
        }
      },
      commands: [{
        stepId: 'semantic-acceptance',
        aspect: 'semantic',
        fileName: result.command.fileName,
        arguments: result.command.arguments,
        expectedResultSha: task.resultSha,
        headBefore: result.actualHead,
        treeBefore: result.treeSha,
        startedAt: result.command.startedAt,
        finishedAt: result.command.finishedAt,
        exitCode: result.command.exitCode,
        signal: null,
        stdoutSha256: result.command.stdoutSha256,
        stderrSha256: result.command.stderrSha256
      }],
      artifacts: [{
        name: 'semantic-acceptance.stdout.log',
        mediaType: 'text/plain',
        sha256: result.command.stdoutSha256,
        sizeBytes: result.command.stdoutSizeBytes
      }, {
        name: 'semantic-acceptance.stderr.log',
        mediaType: 'text/plain',
        sha256: result.command.stderrSha256,
        sizeBytes: result.command.stderrSizeBytes
      }],
      verdicts: [{
        aspect: 'semantic',
        status: 'pass',
        classification: 'Accepted',
        summary: `Deterministic acceptance passed for ${task.taskKey}.`
      }]
    };
    const firstReport = await api.post(
      `/api/v1/reviews/attempts/${claim.attempt.attemptId}/report`,
      reportRequest
    );
    const replayReport = await api.post(
      `/api/v1/reviews/attempts/${claim.attempt.attemptId}/report`,
      reportRequest
    );
    task.review.reportReplay = firstReport.reportId === replayReport.reportId
      && firstReport.reportSha256 === replayReport.reportSha256;
    task.review.reportOutcome = firstReport.outcome;
    task.review.failureClassification = firstReport.failureClassification;
    const attemptRoot = path.resolve(result.workspace, '..');
    await rm(attemptRoot, { recursive: true, force: true });
    task.review.workspaceRemoved = !(await exists(attemptRoot));
    await api.post(`/api/v1/reviews/attempts/${claim.attempt.attemptId}/cleanup`, {
      executorId: task.review.workerId,
      instanceId: task.reviewAssignment.instanceId,
      leaseId: claim.lease.leaseId,
      fence: claim.lease.fence,
      authorityEpoch: claim.lease.authorityEpoch,
      idempotencyKey: `${plan.runId}:${name}:${task.taskKey}:review-cleanup`,
      workspaceRemoved: task.review.workspaceRemoved
    });
  }));

  occupancy.integration.queued = tasks.length;
  const integrationPrepared = await Promise.all(tasks.map(async task => {
    const workspace = path.join(scenarioRoot, 'integration', task.taskKey.toLowerCase());
    await mkdir(path.dirname(workspace), { recursive: true });
    await runCommand(['git', 'clone', origin, workspace], { cwd: path.dirname(workspace) });
    await runCommand(['git', 'config', 'user.name', 'Remote Parallel Integrator'], { cwd: workspace });
    await runCommand(['git', 'config', 'user.email', 'parallel-integration@invalid.local'], { cwd: workspace });
    await runCommand(['git', 'fetch', 'origin', `${task.resultRef}:${task.resultRef}`], { cwd: workspace });
    const fetched = (await runCommand(['git', 'rev-parse', 'FETCH_HEAD'], { cwd: workspace })).stdout.trim();
    if (fetched !== task.resultSha) {
      throw new Error(`Integration fetch drift for ${task.taskKey}: ${fetched} != ${task.resultSha}`);
    }
    const initialBase = (await runCommand(['git', 'rev-parse', 'origin/develop'], { cwd: workspace })).stdout.trim();
    return { task, workspace, initialBase, queuedAt: new Date().toISOString() };
  }));

  const integrationOrder = [];
  let integrationCollisions = 0;
  for (const prepared of deterministicIntegrationOrder(integrationPrepared.map(item => ({
    ...item.task,
    prepared: item
  }))).map(item => item.prepared)) {
    const { task, workspace, initialBase, queuedAt } = prepared;
    occupancy.integration.active = 1;
    occupancy.integration.queued--;
    const admittedAt = new Date().toISOString();
    await runCommand(['git', 'fetch', 'origin', 'develop'], { cwd: workspace });
    const currentBase = (await runCommand(['git', 'rev-parse', 'origin/develop'], { cwd: workspace })).stdout.trim();
    const collision = currentBase !== initialBase;
    if (collision) integrationCollisions++;
    await timeline.append({
      event: 'integration-collision-decision',
      scenario: name,
      taskKey: task.taskKey,
      queuedAt,
      admittedAt,
      staleBase: collision,
      decision: collision ? 'refresh-and-merge-exact-result' : 'merge-exact-result',
      initialBase,
      currentBase
    });
    const executionStart = performance.now();
    await runCommand(['git', 'checkout', '-B', 'develop', 'origin/develop'], { cwd: workspace });
    await runCommand(['git', 'merge', '--no-ff', '--no-edit', task.resultSha], {
      cwd: workspace,
      env: {
        GIT_AUTHOR_DATE: deterministicDate(`${seed}-${name}-integration-${task.ordinal}`),
        GIT_COMMITTER_DATE: deterministicDate(`${seed}-${name}-integration-${task.ordinal}`)
      }
    });
    await runCommand(['node', '--test', 'test/*.test.mjs'], { cwd: workspace });
    await runCommand(['git', 'push', 'origin', 'develop'], { cwd: workspace });
    const integratedHead = (await runCommand(['git', 'rev-parse', 'HEAD'], { cwd: workspace })).stdout.trim();
    const currentTask = await api.get(`/api/v1/projects/${projectId}/tasks/${task.taskId}`);
    await api.put(`/api/v1/projects/${projectId}/tasks/${task.taskId}`, {
      title: null,
      body: null,
      state: '6-completed',
      expectedVersion: currentTask.version
    });
    task.integration = {
      status: 'integrated',
      workspace,
      initialBase,
      admittedBase: currentBase,
      staleBase: collision,
      decision: collision ? 'refresh-and-merge-exact-result' : 'merge-exact-result',
      integratedHead,
      queueWaitMs: Date.parse(admittedAt) - Date.parse(queuedAt),
      executionMs: Math.round((performance.now() - executionStart) * 1000) / 1000
    };
    integrationOrder.push(task.taskKey);
    occupancy.integration.active = 0;
    await runtime.append(runtimeEvent({
      timestamp: new Date().toISOString(),
      level: 'Info',
      event: 'integration.completed',
      subsystem: 'integration',
      operation: 'merge-exact-result',
      correlationId: task.taskKey,
      project: name,
      jobId: task.taskId,
      runId: task.claim.run.runId,
      durationMs: task.integration.executionMs,
      status: 'Ok',
      payload: {
        resultSha: task.resultSha,
        collisionDecision: task.integration.decision,
        queueWaitMs: task.integration.queueWaitMs
      }
    }));
  }

  const finalTasks = await api.get(`/api/v1/projects/${projectId}/tasks`);
  const histories = await Promise.all(tasks.map(task =>
    api.get(`/api/v1/projects/${projectId}/tasks/${task.taskId}/history`)
  ));
  const reviewAttempts = await Promise.all(tasks.map(task =>
    api.get(`/api/v1/reviews/attempts/${task.reviewClaim.attempt.attemptId}`)
  ));
  const audit = await api.get('/api/v1/management/audit');
  const invariants = await api.get('/api/v1/management/invariants');
  sampler.stop();
  await sampler.done;
  await timeline.flush();
  await runtime.flush();
  await telemetry.flush();

  const queueSummary = {
    lost: tasks.length - new Set(finalTasks.map(task => task.taskKey)).size,
    duplicate: tasks.length - new Set(tasks.map(task => task.resultSha)).size,
    pending: finalTasks.filter(task => task.state !== '6-completed').length
  };
  const scenario = {
    name,
    startedAt,
    finishedAt: new Date().toISOString(),
    taskCount: tasks.length,
    peakActiveOrPost,
    tasks: tasks.map(sanitizeTask),
    slotAdmissions: [...slotAdmissions, ...gateBatch.admissions],
    integrationOrder,
    integrationCollisions,
    productFailures: reviewAttempts.filter(attempt => attempt.outcome === 'ProductFailure').length,
    resourceDelays: gateBatch.workerLoss?.retries ?? 0,
    telemetrySamples: samples.length,
    pressure: summarizePressure(samples),
    queueSummary,
    auditSequencesUnique: new Set(audit.map(item => item.sequence)).size === audit.length,
    pendingRunnerActions: invariants.pendingRunnerActions,
    studioHostExecutions: 0,
    workerLoss: gateBatch.workerLoss ?? {
      injected: false,
      retries: 0,
      redistributed: 0,
      maxRetryCount: 0,
      healthyWorkersContinued: true,
      capacityBefore: plan.workerCapacity.gate,
      capacityAfter: plan.workerCapacity.gate
    }
  };
  await writeJson(path.join(evidenceRoot, 'scenario.json'), scenario);
  await writeJson(path.join(evidenceRoot, 'task-histories.json'), histories);
  await writeJson(path.join(evidenceRoot, 'review-attempts.json'), reviewAttempts);
  await writeJson(path.join(evidenceRoot, 'final-tasks.json'), finalTasks);
  await writeJson(path.join(evidenceRoot, 'audit.json'), audit);
  await writeJson(path.join(evidenceRoot, 'invariants.json'), invariants);
  return scenario;
}

async function executeBatch({
  role,
  tasks,
  assignments,
  timeline,
  runtime,
  occupancy,
  evidenceRoot,
  inputFor,
  injectWorkerLoss = false,
  recordAdmissions = true
}) {
  const admissions = [];
  occupancy[role].queued = tasks.length;
  const executions = [];
  for (let index = 0; index < tasks.length; index++) {
    const task = tasks[index];
    const assignment = assignments[index];
    const scheduledAt = new Date().toISOString();
    if (recordAdmissions) {
      const admission = {
        scenario: task.projectId?.includes('worker-loss') ? 'worker-loss' : 'baseline',
        role,
        taskKey: task.taskKey,
        workerId: assignment.workerId,
        slot: assignment.slot,
        availableBefore: assignment.availableBefore,
        activeAfter: assignment.activeAfter,
        poolCapacity: assignmentCapacity(assignments),
        decision: 'admitted',
        decidedAt: scheduledAt
      };
      admissions.push(admission);
      await timeline.append({ event: 'slot-admission', ...admission });
      await runtime.append(runtimeEvent({
        timestamp: scheduledAt,
        level: 'Info',
        event: `${role}.slot-admission.completed`,
        subsystem: role,
        operation: `${role}-slot-admission`,
        correlationId: task.taskKey,
        project: admission.scenario,
        jobId: task.taskId,
        runId: task.claim.run.runId,
        status: 'Ok',
        payload: {
          workerId: assignment.workerId,
          slot: assignment.slot,
          decision: 'admitted',
          availableBefore: assignment.availableBefore
        }
      }));
    }
    const input = inputFor(task, assignment, 0);
    const inputFile = path.join(
      evidenceRoot,
      'executor-inputs',
      role,
      `${task.taskKey.toLowerCase()}-attempt-1.json`
    );
    await writeJson(inputFile, input);
    occupancy[role].active++;
    occupancy[role].queued--;
    const execution = spawnExecutor(inputFile);
    execution.observed = execution.promise.then(
      result => ({ result }),
      error => ({ error })
    );
    executions.push({
      task,
      assignment,
      input,
      inputFile,
      retryCount: 0,
      scheduledAt,
      execution
    });
  }

  let lostWorkerId = null;
  let lossAt = null;
  if (injectWorkerLoss) {
    lostWorkerId = assignments[0].workerId;
    await delay(120);
    lossAt = new Date().toISOString();
    const lostExecutions = executions.filter(item => item.assignment.workerId === lostWorkerId);
    for (const item of lostExecutions) item.execution.child.kill('SIGKILL');
    await timeline.append({
      event: 'worker-loss',
      role,
      workerId: lostWorkerId,
      activeProcesses: lostExecutions.map(item => item.execution.child.pid),
      classification: 'environmental-worker-loss',
      at: lossAt
    });
    await runtime.append(runtimeEvent({
      timestamp: lossAt,
      level: 'Warn',
      event: `${role}.worker-lost`,
      subsystem: role,
      operation: `${role}-worker-execution`,
      status: 'Cancelled',
      payload: {
        workerId: lostWorkerId,
        activeProcesses: lostExecutions.length,
        classification: 'environmental-worker-loss',
        retryable: true
      }
    }));
  }

  const firstWave = await Promise.all(executions.map(async item => {
    const observed = await item.execution.observed;
    if (!observed.error) {
      const result = observed.result;
      occupancy[role].active--;
      await recordExecutorCompletion({ role, item, result, timeline, runtime, retryCount: 0 });
      return { status: 'fulfilled', item, result };
    }
    const error = observed.error;
    occupancy[role].active--;
    const lost = item.assignment.workerId === lostWorkerId;
    await timeline.append({
      event: 'executor-interrupted',
      role,
      taskKey: item.task.taskKey,
      workerId: item.assignment.workerId,
      retryCount: 0,
      classification: lost ? 'environmental-worker-loss' : 'product-or-infrastructure-unknown',
      detail: String(error.message),
      at: new Date().toISOString()
    });
    if (!lost) throw error;
    return { status: 'rejected', item, error };
  }));

  const results = firstWave.filter(item => item.status === 'fulfilled').map(item => item.result);
  const retryItems = firstWave.filter(item => item.status === 'rejected');
  const healthyAssignments = assignments.filter(item => item.workerId !== lostWorkerId);
  let healthyCursor = 0;
  for (const rejected of retryItems) {
    const task = rejected.item.task;
    const original = rejected.item.assignment;
    const healthy = healthyAssignments[healthyCursor++ % healthyAssignments.length];
    const reassigned = {
      ...healthy,
      slot: original.slot,
      availableBefore: Math.max(1, healthy.availableBefore),
      decision: 'admitted'
    };
    const retryAt = new Date().toISOString();
    const admission = {
      scenario: 'worker-loss',
      role,
      taskKey: task.taskKey,
      workerId: reassigned.workerId,
      slot: reassigned.slot,
      availableBefore: reassigned.availableBefore,
      activeAfter: reassigned.activeAfter,
      poolCapacity: assignmentCapacity(healthyAssignments),
      decision: 'admitted',
      retryCount: 1,
      reason: 'environmental-worker-loss',
      decidedAt: retryAt
    };
    admissions.push(admission);
    await timeline.append({ event: 'slot-admission', ...admission });
    const input = inputFor(task, reassigned, 1);
    await rm(input.workspace, { recursive: true, force: true });
    const inputFile = path.join(
      evidenceRoot,
      'executor-inputs',
      role,
      `${task.taskKey.toLowerCase()}-attempt-2.json`
    );
    await writeJson(inputFile, input);
    occupancy[role].active++;
    const execution = spawnExecutor(inputFile);
    const result = await execution.promise;
    occupancy[role].active--;
    await recordExecutorCompletion({
      role,
      item: {
        task,
        assignment: reassigned,
        scheduledAt: retryAt
      },
      result,
      timeline,
      runtime,
      retryCount: 1
    });
    results.push(result);
  }

  const sorted = [...results].sort((left, right) => left.ordinal - right.ordinal);
  return {
    results: sorted,
    admissions,
    workerLoss: injectWorkerLoss ? {
      injected: true,
      workerId: lostWorkerId,
      lostAt: lossAt,
      classification: 'environmental-worker-loss',
      retries: retryItems.length,
      redistributed: retryItems.length,
      maxRetryCount: retryItems.length > 0 ? 1 : 0,
      healthyWorkersContinued: firstWave.some(item =>
        item.status === 'fulfilled' && item.result.finishedAt > lossAt),
      capacityBefore: new Set(assignments.map(item => item.workerId)).size
        * Math.max(...assignments.map(item => item.slot)),
      capacityAfter: new Set(healthyAssignments.map(item => item.workerId)).size
        * Math.max(...healthyAssignments.map(item => item.slot))
    } : null
  };
}

async function recordExecutorCompletion({ role, item, result, timeline, runtime, retryCount }) {
  const queueWaitMs = Date.parse(result.startedAt) - Date.parse(item.scheduledAt);
  result.queueWaitMs = Math.max(0, queueWaitMs);
  result.retryCount = retryCount;
  await timeline.append({
    event: 'executor-completed',
    role,
    taskKey: item.task.taskKey,
    workerId: result.workerId,
    hostId: result.hostId,
    slot: result.slot,
    processId: result.processId,
    retryCount,
    queueWaitMs: result.queueWaitMs,
    executionMs: result.durationMs,
    expectedResultSha: result.expectedResultSha ?? result.resultSha,
    actualHead: result.actualHead ?? result.resultSha,
    at: result.finishedAt
  });
  await runtime.append(runtimeEvent({
    timestamp: result.finishedAt,
    level: 'Info',
    event: `${role}.execution.completed`,
    subsystem: role,
    operation: `${role}-worker-execution`,
    correlationId: item.task.taskKey,
    project: item.task.projectId?.includes('worker-loss') ? 'worker-loss' : 'baseline',
    jobId: item.task.taskId,
    runId: item.task.claim.run.runId,
    durationMs: result.durationMs,
    status: 'Ok',
    payload: {
      workerId: result.workerId,
      hostId: result.hostId,
      slot: result.slot,
      processId: result.processId,
      retryCount,
      queueWaitMs: result.queueWaitMs,
      resultSha: result.expectedResultSha ?? result.resultSha
    }
  }));
}

function spawnExecutor(inputFile) {
  const child = spawn(process.execPath, [
    path.join(suiteRoot, 'parallel-executor.mjs'),
    '--input',
    inputFile
  ], {
    cwd: repoRoot,
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let stdout = '';
  let stderr = '';
  const promise = new Promise((resolve, reject) => {
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (code, signal) => {
      if (code === 0) {
        try {
          resolve(JSON.parse(stdout.trim()));
        } catch (error) {
          reject(new Error(`Executor returned invalid JSON: ${error.message}; stdout=${stdout}`));
        }
        return;
      }
      reject(new Error(
        `Executor ${path.basename(inputFile)} exited code=${code} signal=${signal}: ${stderr || stdout}`
      ));
    });
  });
  return { child, promise };
}

async function prepareCodingWorktrees(scenarioRoot, origin, tasks, assignments) {
  const caches = new Map();
  for (const assignment of assignments) {
    if (caches.has(assignment.workerId)) continue;
    const cache = path.join(scenarioRoot, 'coding', assignment.workerId, 'repository-cache');
    await mkdir(path.dirname(cache), { recursive: true });
    await runCommand(['git', 'clone', origin, cache], { cwd: path.dirname(cache) });
    await runCommand(['git', 'config', 'user.name', 'Remote Parallel Coding Executor'], { cwd: cache });
    await runCommand(['git', 'config', 'user.email', 'remote-parallel-coding@invalid.local'], { cwd: cache });
    caches.set(assignment.workerId, cache);
  }
  for (const task of tasks) {
    const assignment = assignmentFor(assignments, task.ordinal);
    const cache = caches.get(assignment.workerId);
    const workspace = path.join(
      scenarioRoot,
      'coding',
      assignment.workerId,
      'worktrees',
      task.taskKey.toLowerCase()
    );
    const branch = `runner/${assignment.workerId}/${task.taskKey.toLowerCase()}`;
    await mkdir(path.dirname(workspace), { recursive: true });
    await runCommand([
      'git', 'worktree', 'add', '-b', branch, workspace, 'origin/develop'
    ], { cwd: cache });
    task.coding.workspace = workspace;
    task.coding.branch = branch;
    task.coding.cache = cache;
  }
}

async function seedFixture(scenarioRoot, scenario, seed) {
  const seedRepo = path.join(scenarioRoot, 'fixture-seed');
  const origin = path.join(scenarioRoot, 'fixture-origin.git');
  await mkdir(seedRepo, { recursive: true });
  await runCommand(['git', 'init', '-b', 'develop'], { cwd: seedRepo });
  await runCommand(['git', 'config', 'user.name', 'Parallel Remote Test Suite'], { cwd: seedRepo });
  await runCommand(['git', 'config', 'user.email', 'parallel-remote-test@invalid.local'], { cwd: seedRepo });
  await mkdir(path.join(seedRepo, 'test'), { recursive: true });
  await writeFile(path.join(seedRepo, 'package.json'), `${JSON.stringify({
    name: 'parallel-remote-reference-fixture',
    private: true,
    type: 'module',
    scripts: { test: 'node --test test/*.test.mjs' }
  }, null, 2)}\n`);
  await writeFile(path.join(seedRepo, 'test', 'baseline.test.mjs'), [
    "import test from 'node:test';",
    "import assert from 'node:assert/strict';",
    `test('baseline ${scenario}', () => assert.equal('${seed}'.length > 0, true));`,
    ''
  ].join('\n'));
  await writeFile(path.join(seedRepo, 'README.md'), [
    '# Parallel remote reference fixture',
    '',
    `Scenario: ${scenario}. Seed: ${seed}.`,
    ''
  ].join('\n'));
  await runCommand(['git', 'add', '.'], { cwd: seedRepo });
  await runCommand(['git', 'commit', '-m', 'fixture: parallel deterministic baseline'], {
    cwd: seedRepo,
    env: {
      GIT_AUTHOR_DATE: deterministicDate(`${seed}-${scenario}-baseline`),
      GIT_COMMITTER_DATE: deterministicDate(`${seed}-${scenario}-baseline`)
    }
  });
  await runCommand(['git', 'init', '--bare', origin], { cwd: scenarioRoot });
  await runCommand(['git', 'remote', 'add', 'origin', origin], { cwd: seedRepo });
  await runCommand(['git', 'push', '-u', 'origin', 'develop'], { cwd: seedRepo });
  await runCommand(['git', '--git-dir', origin, 'symbolic-ref', 'HEAD', 'refs/heads/develop'], {
    cwd: scenarioRoot
  });
  return origin;
}

async function registerWorkers(api, assignments, capability) {
  const seen = new Set();
  for (const assignment of assignments) {
    if (seen.has(assignment.workerId)) continue;
    seen.add(assignment.workerId);
    const capabilities = capability === 'review-executor'
      ? ['review-executor', 'review:git', 'review:semantic']
      : [capability];
    await api.put(`/api/v1/runners/${assignment.workerId}`, {
      name: assignment.workerId,
      hostId: assignment.hostId,
      instanceId: assignment.instanceId,
      runnerVersion: 'parallel-remote-test-suite/1',
      protocolVersion: 2,
      capabilities
    });
  }
}

function qualifyAssignments(assignments, runId, scenario) {
  return assignments.map(assignment => {
    const workerId = `${runId}-${scenario}-${assignment.workerId}`;
    return {
      ...assignment,
      workerId,
      instanceId: `${workerId}-instance`,
      hostId: `${workerId}-host`
    };
  });
}

function assignmentFor(assignments, ordinal) {
  const assignment = assignments.find(item => item.ordinal === ordinal);
  if (!assignment) throw new Error(`No worker assignment for ordinal ${ordinal}`);
  return assignment;
}

function assignmentCapacity(assignments) {
  if (assignments.length === 0) return 0;
  return new Set(assignments.map(item => item.workerId)).size
    * Math.max(...assignments.map(item => item.slot));
}

function repositoryIdentity(repositoryUrl) {
  const normalized = repositoryUrl.trim().replace(/\/$/, '').toLowerCase();
  return `repo_${sha256(normalized)}`;
}

function deterministicDate(seed) {
  const seconds = Number.parseInt(createHash('sha256').update(seed).digest('hex').slice(0, 8), 16)
    % (20 * 365 * 24 * 60 * 60);
  return new Date(Date.UTC(2020, 0, 1) + seconds * 1000).toISOString();
}

function runtimeEvent({
  timestamp,
  level,
  event,
  subsystem,
  operation = null,
  correlationId = null,
  project = null,
  jobId = null,
  runId = null,
  durationMs = null,
  status = null,
  payload = null
}) {
  return {
    schemaVersion: 1,
    timestamp,
    level,
    event,
    subsystem,
    operation,
    correlationId,
    project,
    jobId,
    runId,
    duration: durationMs === null ? null : { ms: durationMs },
    status,
    payload
  };
}

function startSampler(writer, occupancy, samples) {
  let stopped = false;
  let previousCpu = null;
  const done = (async () => {
    while (!stopped) {
      const cpu = await readCpuSample();
      const cpuPercent = previousCpu ? cpuUsagePercent(previousCpu, cpu) : 0;
      previousCpu = cpu;
      const memoryUsed = os.totalmem() - os.freemem();
      const sample = {
        timestamp: new Date().toISOString(),
        cpuPercent,
        load1: os.loadavg()[0],
        memoryUsedBytes: memoryUsed,
        memoryPressurePercent: Math.round(memoryUsed / os.totalmem() * 10000) / 100,
        residentBytes: process.memoryUsage().rss,
        occupancy: structuredClone(occupancy)
      };
      samples.push(sample);
      await writer.append(sample);
      await delay(100);
    }
  })();
  return {
    stop: () => { stopped = true; },
    done
  };
}

async function readCpuSample() {
  const line = (await readFile('/proc/stat', 'utf8')).split(/\r?\n/)[0];
  const values = line.trim().split(/\s+/).slice(1).map(Number);
  return {
    idle: values[3] + (values[4] ?? 0),
    total: values.reduce((sum, value) => sum + value, 0)
  };
}

function cpuUsagePercent(previous, current) {
  const total = current.total - previous.total;
  const idle = current.idle - previous.idle;
  return total <= 0 ? 0 : Math.round((1 - idle / total) * 10000) / 100;
}

function sanitizeTask(task) {
  return {
    ordinal: task.ordinal,
    taskId: task.taskId,
    taskKey: task.taskKey,
    resultSha: task.resultSha,
    resultRef: task.resultRef,
    commitTaskKey: task.commitTaskKey,
    crossTaskCommits: task.crossTaskCommits,
    handoffReplay: task.handoffReplay,
    completionReplay: task.completionReplay,
    coding: task.coding,
    gate: task.gate,
    review: task.review,
    integration: task.integration
  };
}

function renderReport(acceptance) {
  const lines = [
    '# Parallel remote delivery verification',
    '',
    `Run: \`${acceptance.runId}\`. Seed: \`${acceptance.seed}\`.`,
    '',
    `Verdict: ${acceptance.accepted ? 'accepted' : 'failed'}. This is infrastructure verification only; no model or CLI comparison dimension was recorded.`,
    '',
    '| Scenario | Tasks | Peak active or post | Coding workers | Gate workers | Review workers | Environmental retries | Integration collisions |',
    '|---|---:|---:|---:|---:|---:|---:|---:|'
  ];
  for (const scenario of acceptance.scenarios) {
    lines.push(
      `| ${scenario.name} | ${scenario.tasks.length} | ${scenario.peakActiveOrPost} | ` +
      `${new Set(scenario.tasks.map(task => task.coding.workerId)).size} | ` +
      `${new Set(scenario.tasks.map(task => task.gate.workerId)).size} | ` +
      `${new Set(scenario.tasks.map(task => task.review.workerId)).size} | ` +
      `${scenario.workerLoss?.retries ?? 0} | ${scenario.integrationCollisions} |`
    );
  }
  lines.push(
    '',
    '## Acceptance',
    '',
    ...acceptance.assertions.map(assertion =>
      `- ${assertion.passed ? 'PASS' : 'FAIL'} \`${assertion.name}\`: ${assertion.detail}`
    ),
    '',
    '## Evidence map',
    '',
    '- `baseline/timeline.jsonl` and `worker-loss/timeline.jsonl`: concurrency, admission, retry, and collision decisions.',
    '- `baseline/telemetry.jsonl` and `worker-loss/telemetry.jsonl`: CPU, memory, load, queue depth, and slot occupancy.',
    '- `*/runtime-events.jsonl`: schema-shaped runtime events for read-only runtime log analysis.',
    '- `*/task-histories.json`, `*/review-attempts.json`, and `*/audit.json`: Task Server authority and idempotency evidence.',
    '- `acceptance.json`: machine-readable invariant result.',
    ''
  );
  return `${lines.join('\n')}\n`;
}

function parseArgs(values) {
  const result = {
    runId: '',
    seed: 'parallel-reference-v1',
    taskCount: parallelDefaults.taskCount,
    codingWorkers: parallelDefaults.codingWorkers,
    gateWorkers: parallelDefaults.gateWorkers,
    reviewWorkers: parallelDefaults.reviewWorkers,
    slotsPerWorker: parallelDefaults.slotsPerWorker,
    dryRun: false,
    cleanupOnFailure: false
  };
  for (let index = 0; index < values.length; index++) {
    const key = values[index];
    if (key === '--dry-run') result.dryRun = true;
    else if (key === '--cleanup-on-failure') result.cleanupOnFailure = true;
    else if (key.startsWith('--')) {
      const name = key.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
      result[name] = values[++index];
    }
  }
  if (!result.runId) {
    throw new Error('Usage: parallel-harness.mjs --run-id UNIQUE_ID [--seed SEED] [--export-root PATH]');
  }
  if (!/^[A-Za-z0-9._-]{1,80}$/.test(result.seed)) {
    throw new Error('Seed must use only letters, digits, dot, underscore, or hyphen.');
  }
  for (const name of [
    'taskCount',
    'codingWorkers',
    'gateWorkers',
    'reviewWorkers',
    'slotsPerWorker'
  ]) {
    result[name] = Number(result[name]);
  }
  return result;
}

async function availablePort() {
  return await new Promise((resolve, reject) => {
    const server = createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => resolve(address.port));
    });
  });
}

async function startTaskServer(root, serverUrl) {
  const child = spawn('dotnet', [
    'run',
    '--project',
    path.join(repoRoot, 'task-server'),
    '--no-launch-profile'
  ], {
    cwd: repoRoot,
    env: {
      ...process.env,
      LISTEN_URL: serverUrl,
      TaskServer__DataDirectory: path.join(root, 'task-server-data'),
      TaskServer__BackupDirectory: path.join(root, 'task-server-backups'),
      TaskServer__MinimumLeaseSeconds: '5',
      TaskServer__MaximumLeaseSeconds: '120'
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  const output = { value: '' };
  child.stdout.on('data', chunk => { output.value += chunk; });
  child.stderr.on('data', chunk => { output.value += chunk; });
  for (let attempt = 0; attempt < 240; attempt++) {
    if (child.exitCode !== null) throw new Error(`Task Server exited during startup: ${output.value}`);
    try {
      const response = await fetch(`${serverUrl}/readyz`);
      if (response.ok) return { child, output };
    } catch {
      // Startup polling is bounded.
    }
    await delay(250);
  }
  child.kill('SIGTERM');
  throw new Error(`Task Server did not become ready: ${output.value}`);
}

async function exists(file) {
  try {
    await stat(file);
    return true;
  } catch {
    return false;
  }
}

async function writeJson(file, value) {
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, `${JSON.stringify(value, null, 2)}\n`);
}

function redactTaskServerOutput(value) {
  return String(value).replace(/Bearer\s+[A-Za-z0-9._~-]+/g, 'Bearer [REDACTED]');
}

function assertSafeExportRoot(exportRoot) {
  const parsedRoot = path.parse(exportRoot).root;
  const normalized = path.resolve(exportRoot);
  const forbiddenExact = new Set([
    parsedRoot,
    path.resolve(repoRoot),
    path.resolve(baseRoot),
    process.env.HOME ? path.resolve(process.env.HOME) : null
  ].filter(Boolean));
  if (forbiddenExact.has(normalized)
      || path.dirname(normalized) === parsedRoot
      || /(^|[/\\])agent-taskboard-stable([/\\]|$)/.test(normalized)
      || /agent-taskboard-workspace[/\\](projects|\.metadata)([/\\]|$)/.test(normalized)) {
    throw new Error(`Refusing unsafe parallel evidence export root: ${normalized}`);
  }
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
