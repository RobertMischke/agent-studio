import path from 'node:path';

export const parallelDefaults = Object.freeze({
  taskCount: 12,
  codingWorkers: 3,
  gateWorkers: 3,
  reviewWorkers: 3,
  slotsPerWorker: 4
});

export function validateParallelOptions(options) {
  const normalized = {
    taskCount: positiveInteger(options.taskCount, 'taskCount'),
    codingWorkers: positiveInteger(options.codingWorkers, 'codingWorkers'),
    gateWorkers: positiveInteger(options.gateWorkers, 'gateWorkers'),
    reviewWorkers: positiveInteger(options.reviewWorkers, 'reviewWorkers'),
    slotsPerWorker: positiveInteger(options.slotsPerWorker, 'slotsPerWorker')
  };
  if (normalized.taskCount < 12) {
    throw new Error('Parallel delivery verification requires at least 12 tasks.');
  }
  if (normalized.codingWorkers < 2 || normalized.gateWorkers < 2 || normalized.reviewWorkers < 2) {
    throw new Error('Coding, gate, and review execution each require at least two workers.');
  }
  if (normalized.codingWorkers * normalized.slotsPerWorker < 10) {
    throw new Error('Coding capacity must expose at least ten simultaneously admissible slots.');
  }
  for (const role of ['coding', 'gate', 'review']) {
    if (normalized[`${role}Workers`] * normalized.slotsPerWorker < normalized.taskCount) {
      throw new Error(`${role} worker capacity must cover every task in the concurrent batch.`);
    }
  }
  return normalized;
}

export function createParallelPlan({ baseRoot, runId, options = parallelDefaults }) {
  if (!/^[A-Za-z0-9._-]{1,80}$/.test(runId ?? '')) {
    throw new Error('Run id must use only letters, digits, dot, underscore, or hyphen.');
  }
  const config = validateParallelOptions({ ...parallelDefaults, ...options });
  const root = path.resolve(baseRoot, 'parallel-delivery', runId);
  const relative = path.relative(path.resolve(baseRoot), root);
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) {
    throw new Error(`Refusing unscoped parallel evidence root: ${root}`);
  }
  if (/(^|[/\\])agent-taskboard-stable([/\\]|$)/.test(root)
      || /agent-taskboard-workspace[/\\](projects|\.metadata)([/\\]|$)/.test(root)) {
    throw new Error(`Refusing protected parallel evidence root: ${root}`);
  }
  return {
    runId,
    root,
    config,
    scenarios: ['baseline', 'worker-loss'],
    evidenceRoot: path.join(root, 'evidence'),
    neverTouches: [
      'agent-taskboard-stable/',
      'agent-taskboard-workspace/projects/',
      'agent-taskboard-workspace/.metadata/'
    ],
    workerCapacity: {
      coding: config.codingWorkers * config.slotsPerWorker,
      gate: config.gateWorkers * config.slotsPerWorker,
      review: config.reviewWorkers * config.slotsPerWorker
    }
  };
}

export function workerAssignments(count, role, workerCount, slotsPerWorker) {
  const assignments = [];
  const occupancy = new Map();
  for (let ordinal = 1; ordinal <= count; ordinal++) {
    const workerIndex = (ordinal - 1) % workerCount;
    const workerId = `${role}-worker-${workerIndex + 1}`;
    const slot = Math.floor((ordinal - 1) / workerCount) % slotsPerWorker + 1;
    const activeBefore = occupancy.get(workerId) ?? 0;
    occupancy.set(workerId, activeBefore + 1);
    assignments.push({
      ordinal,
      workerId,
      workerIndex,
      slot,
      activeBefore,
      activeAfter: activeBefore + 1,
      availableBefore: slotsPerWorker - activeBefore,
      decision: activeBefore < slotsPerWorker ? 'admitted' : 'deferred'
    });
  }
  return assignments;
}

export function deterministicIntegrationOrder(tasks) {
  return [...tasks].sort((left, right) => left.ordinal - right.ordinal);
}

export function assertParallelEvidence(evidence) {
  const assertions = [];
  const scenarios = evidence.scenarios ?? [];
  check(assertions, 'two-scenarios-recorded', scenarios.length === 2,
    'Baseline and controlled worker-loss scenarios must both be recorded.');

  for (const scenario of scenarios) {
    const prefix = scenario.name;
    const tasks = scenario.tasks ?? [];
    check(assertions, `${prefix}:at-least-twelve-tasks`, tasks.length >= 12,
      'Each scenario must contain at least twelve isolated reference tasks.');
    check(assertions, `${prefix}:ten-active-or-post`, scenario.peakActiveOrPost >= 10,
      'At least ten cards must be observed in active or post-processing state together.');
    check(assertions, `${prefix}:slot-decisions-complete`,
      (scenario.slotAdmissions ?? []).length >= tasks.length * 3
        && (scenario.slotAdmissions ?? []).every(item => item.decision === 'admitted'),
      'Every coding, gate, and review execution needs a recorded admission decision.');
    check(assertions, `${prefix}:multi-worker-execution`,
      ['coding', 'gate', 'review'].every(role =>
        new Set(tasks.map(task => task[role]?.workerId).filter(Boolean)).size > 1),
      'Coding, gates, and reviews must each use more than one worker.');
    check(assertions, `${prefix}:isolated-workspaces`,
      unique(tasks.flatMap(task => [
        task.coding?.workspace,
        task.gate?.workspace,
        task.review?.workspace
      ].filter(Boolean)))
        && tasks.every(task =>
          task.gate?.workspaceRemoved === true
          && task.review?.workspaceRemoved === true),
      'Workspace paths must be unique and disposable gate/review roots must be removed.');
    check(assertions, `${prefix}:exact-result-sha`,
      tasks.every(task =>
        fullSha(task.resultSha)
        && task.gate?.expectedResultSha === task.resultSha
        && task.gate?.actualHead === task.resultSha
        && task.review?.expectedResultSha === task.resultSha
        && task.review?.actualHead === task.resultSha),
      'Every gate and review must execute at its declared Result SHA.');
    check(assertions, `${prefix}:delivery-idempotent`,
      tasks.every(task => task.handoffReplay === true && task.completionReplay === true),
      'Result handoff and completion must replay idempotently.');
    check(assertions, `${prefix}:review-idempotent`,
      tasks.every(task =>
        task.review?.reportReplay === true
        && task.review?.reportOutcome === 'Pass'
        && task.review?.failureClassification === null),
      'Review verdict replay must retain one authoritative passing report.');
    check(assertions, `${prefix}:integration-deterministic`,
      tasks.every(task => task.integration?.status === 'integrated')
        && (scenario.integrationOrder ?? []).join(',') ===
           deterministicIntegrationOrder(tasks).map(task => task.taskKey).join(','),
      'Integration must complete in stable task ordinal order.');
    check(assertions, `${prefix}:no-cross-task-commits`,
      tasks.every(task => task.commitTaskKey === task.taskKey && task.crossTaskCommits === 0),
      'No result range may contain a commit attributed to another task.');
    check(assertions, `${prefix}:no-product-failures`, scenario.productFailures === 0,
      'Environmental pressure or worker loss must not be counted as product failure.');
    check(assertions, `${prefix}:telemetry-present`,
      (scenario.telemetrySamples ?? 0) > 0
        && Number.isFinite(scenario.pressure?.maxMemoryPressurePercent)
        && Number.isFinite(scenario.pressure?.maxLoad1),
      'Resource pressure and slot occupancy telemetry must be present.');
    check(assertions, `${prefix}:queues-drained`,
      scenario.queueSummary?.lost === 0
        && scenario.queueSummary?.duplicate === 0
        && scenario.queueSummary?.pending === 0,
      'No queue item may be lost, duplicated, or left pending.');
    check(assertions, `${prefix}:authority-audit-clear`,
      scenario.auditSequencesUnique === true
        && scenario.pendingRunnerActions === 0
        && scenario.studioHostExecutions === 0,
      'Audit sequences must be unique, authority actions drained, and Studio must execute no work.');
  }

  const loss = scenarios.find(item => item.name === 'worker-loss');
  check(assertions, 'worker-loss:injected',
    loss?.workerLoss?.injected === true && loss.workerLoss.workerId,
    'The repeated scenario must record one controlled worker loss.');
  check(assertions, 'worker-loss:bounded-redistribution',
    loss?.workerLoss?.classification === 'environmental-worker-loss'
      && loss.workerLoss.retries > 0
      && loss.workerLoss.retries === loss.workerLoss.redistributed
      && loss.workerLoss.maxRetryCount === 1
      && loss.workerLoss.healthyWorkersContinued === true,
    'Lost gate work must redistribute once while healthy workers continue.');
  check(assertions, 'worker-loss:honest-capacity',
    loss?.workerLoss?.capacityAfter < loss?.workerLoss?.capacityBefore,
    'Evidence must show the reduced eligible capacity after worker loss.');
  return assertions;
}

export function summarizePressure(samples) {
  const numeric = (field) => samples.map(sample => Number(sample[field])).filter(Number.isFinite);
  return {
    maxCpuPercent: max(numeric('cpuPercent')),
    maxMemoryPressurePercent: max(numeric('memoryPressurePercent')),
    maxLoad1: max(numeric('load1')),
    maxResidentBytes: max(numeric('residentBytes'))
  };
}

function positiveInteger(value, name) {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    throw new Error(`${name} must be a positive integer.`);
  }
  return parsed;
}

function fullSha(value) {
  return /^[0-9a-f]{40}$/.test(value ?? '');
}

function unique(values) {
  return new Set(values).size === values.length;
}

function max(values) {
  return values.length === 0 ? 0 : Math.max(...values);
}

function check(assertions, name, passed, detail) {
  assertions.push({ name, passed, detail });
  if (!passed) throw new Error(`Parallel harness assertion failed: ${name}. ${detail}`);
}
