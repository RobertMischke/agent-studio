import test from 'node:test';
import assert from 'node:assert/strict';
import {
  assertParallelEvidence,
  createParallelPlan,
  deterministicIntegrationOrder,
  summarizePressure,
  validateParallelOptions,
  workerAssignments
} from '../parallel-core.mjs';

test('parallel plan requires twelve tasks and horizontally scaled role pools', () => {
  const plan = createParallelPlan({
    baseRoot: '/tmp/remote-suite',
    runId: 'parallel-01',
    options: {
      taskCount: 12,
      codingWorkers: 3,
      gateWorkers: 3,
      reviewWorkers: 3,
      slotsPerWorker: 4
    }
  });
  assert.match(plan.root, /parallel-delivery[/\\]parallel-01$/);
  assert.equal(plan.workerCapacity.coding, 12);
  assert.deepEqual(plan.scenarios, ['baseline', 'worker-loss']);
  assert.throws(() => validateParallelOptions({
    taskCount: 11,
    codingWorkers: 3,
    gateWorkers: 3,
    reviewWorkers: 3,
    slotsPerWorker: 4
  }), /at least 12/);
  assert.throws(() => validateParallelOptions({
    taskCount: 12,
    codingWorkers: 1,
    gateWorkers: 3,
    reviewWorkers: 3,
    slotsPerWorker: 12
  }), /at least two workers/);
  assert.throws(() => createParallelPlan({
    baseRoot: '/tmp/agent-taskboard-stable',
    runId: 'parallel-01'
  }), /protected parallel evidence root/);
});

test('slot assignments are stable and expose occupancy decisions', () => {
  const assignments = workerAssignments(12, 'coding', 3, 4);
  assert.equal(assignments.length, 12);
  assert.deepEqual(assignments.slice(0, 4).map(item => item.workerId), [
    'coding-worker-1',
    'coding-worker-2',
    'coding-worker-3',
    'coding-worker-1'
  ]);
  assert.equal(assignments.at(-1).slot, 4);
  assert.ok(assignments.every(item => item.decision === 'admitted'));
});

test('integration order is task ordinal, independent of review arrival', () => {
  assert.deepEqual(
    deterministicIntegrationOrder([
      { ordinal: 3, taskKey: 'RTS-3' },
      { ordinal: 1, taskKey: 'RTS-1' },
      { ordinal: 2, taskKey: 'RTS-2' }
    ]).map(item => item.taskKey),
    ['RTS-1', 'RTS-2', 'RTS-3']
  );
});

test('pressure summary retains maxima without deriving slot capacity from CPU', () => {
  assert.deepEqual(summarizePressure([
    { cpuPercent: 50, memoryPressurePercent: 20, load1: 1.2, residentBytes: 100 },
    { cpuPercent: 85, memoryPressurePercent: 25, load1: 2.4, residentBytes: 150 }
  ]), {
    maxCpuPercent: 85,
    maxMemoryPressurePercent: 25,
    maxLoad1: 2.4,
    maxResidentBytes: 150
  });
});

test('parallel evidence asserts exact SHA, worker loss, and deterministic delivery', () => {
  const sha = 'a'.repeat(40);
  const task = ordinal => ({
    ordinal,
    taskKey: `RTS-${ordinal}`,
    resultSha: sha,
    commitTaskKey: `RTS-${ordinal}`,
    crossTaskCommits: 0,
    handoffReplay: true,
    completionReplay: true,
    coding: { workerId: `coding-worker-${(ordinal % 2) + 1}`, workspace: `/coding/${ordinal}` },
    gate: {
      workerId: `gate-worker-${(ordinal % 2) + 1}`,
      workspace: `/gate/${ordinal}`,
      expectedResultSha: sha,
      actualHead: sha,
      workspaceRemoved: true
    },
    review: {
      workerId: `review-worker-${(ordinal % 2) + 1}`,
      workspace: `/review/${ordinal}`,
      expectedResultSha: sha,
      actualHead: sha,
      reportReplay: true,
      reportOutcome: 'Pass',
      failureClassification: null,
      workspaceRemoved: true
    },
    integration: { status: 'integrated' }
  });
  const scenario = name => ({
    name,
    tasks: Array.from({ length: 12 }, (_, index) => task(index + 1)),
    peakActiveOrPost: 12,
    slotAdmissions: Array.from({ length: 36 }, () => ({ decision: 'admitted' })),
    integrationOrder: Array.from({ length: 12 }, (_, index) => `RTS-${index + 1}`),
    productFailures: 0,
    telemetrySamples: 2,
    pressure: { maxMemoryPressurePercent: 20, maxLoad1: 1 },
    queueSummary: { lost: 0, duplicate: 0, pending: 0 },
    auditSequencesUnique: true,
    pendingRunnerActions: 0,
    studioHostExecutions: 0
  });
  const loss = scenario('worker-loss');
  loss.workerLoss = {
    injected: true,
    workerId: 'gate-worker-1',
    classification: 'environmental-worker-loss',
    retries: 4,
    redistributed: 4,
    maxRetryCount: 1,
    healthyWorkersContinued: true,
    capacityBefore: 12,
    capacityAfter: 8
  };
  const evidence = { scenarios: [scenario('baseline'), loss] };
  assert.ok(assertParallelEvidence(evidence).every(item => item.passed));
  loss.tasks[0].review.actualHead = 'b'.repeat(40);
  assert.throws(() => assertParallelEvidence(evidence), /exact-result-sha/);
});
