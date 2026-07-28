import test from 'node:test';
import assert from 'node:assert/strict';
import {
  assertRollingEvidence,
  composeCommand,
  createComposePlan,
  redact,
  unitOperationPlan,
  validatePorts,
  validateRunId
} from '../compose-core.mjs';

test('Compose plan gives every Docker resource an explicit harness identity', () => {
  const plan = createComposePlan({
    repoRoot: '/work/agent-studio',
    runId: 'smoke-01',
    ports: { taskServer: 21001, studio: 21002, faultControl: 21003, runnerControl: 21004 }
  });
  assert.equal(plan.project, 'agt2394-rts-smoke-01');
  assert.equal(plan.resources.network, `${plan.project}-network`);
  assert.ok(plan.resources.volumes.every(value => value.startsWith(plan.project)));
  assert.ok(plan.resources.images.every(value => value.startsWith(plan.project)));
  assert.match(plan.identityLabel, /agt2394-rts-smoke-01/);
  assert.ok(plan.neverTouches.includes('agent-studio'));
  assert.deepEqual(composeCommand(plan, 'config', '--quiet').slice(-2), ['config', '--quiet']);
});

test('run ids and port blocks fail closed before Docker is invoked', () => {
  assert.equal(validateRunId('host-01'), 'host-01');
  assert.throws(() => validateRunId('../stable'), /Run id/);
  assert.throws(() => validateRunId('UPPER'), /Run id/);
  assert.throws(() => validatePorts({
    taskServer: 20000,
    studio: 20000,
    faultControl: 20002,
    runnerControl: 20003
  }), /unique/);
});

test('Task Server replacement plans drain and readiness preparation first', () => {
  const guarded = unitOperationPlan('replace', 'task-server');
  assert.deepEqual(guarded.map(step => step.kind), [
    'api', 'prepare-shutdown', 'compose', 'wait-ready', 'api'
  ]);
  assert.equal(guarded[1].allowUnsafe, false);
  assert.equal(guarded[2].args.at(-1), 'task-server');
  assert.equal(guarded.at(-1).body.mode, 0);

  const forced = unitOperationPlan('replace', 'task-server', { force: true });
  assert.equal(forced[1].allowUnsafe, true);
  assert.throws(() => unitOperationPlan('replace', 'database'), /Unit must be/);
});

test('network partitions map to only the selected unit links', () => {
  assert.deepEqual(
    unitOperationPlan('partition', 'runner').map(step => step.route),
    ['/links/runner/partition']
  );
  assert.deepEqual(
    unitOperationPlan('partition', 'studio').map(step => step.route),
    ['/links/studio/partition']
  );
  assert.deepEqual(
    unitOperationPlan('partition', 'task-server').map(step => step.route),
    ['/links/runner/partition', '/links/studio/partition']
  );
  assert.equal(unitOperationPlan('restart', 'runner').at(-1).kind, 'wait-unit');
  assert.equal(unitOperationPlan('restart', 'studio').at(-1).kind, 'wait-unit');
});

test('rolling evidence accepts preservation plus honest Task Server fencing', () => {
  const run1 = { runId: 'run-1', fence: 1, status: 'process-unknown' };
  const run2 = { runId: 'run-2', fence: 2, status: 'released' };
  const evidence = {
    reference: { accepted: true },
    active: { task: { state: '3-progress' }, runs: [{ ...run1, status: 'running' }] },
    afterStudio: { task: { state: '3-progress' }, runs: [{ ...run1, status: 'running' }] },
    afterRunner: { task: { state: '3-progress' }, runs: [{ ...run1, status: 'running' }] },
    quarantined: { task: { state: '3-progress' }, runs: [run1] },
    staleWriteStatus: 409,
    finalHistory: { task: { state: '2-ready' }, runs: [run1, run2] },
    finalShutdown: { safeToStop: true, unresolvedAttempts: 0 },
    status: { outboxBacklog: 0 },
    outboxes: [{ backlogCount: 0 }],
    audit: [{ sequence: 1 }, { sequence: 2 }],
    invariants: { pendingRunnerActions: 0 }
  };
  const assertions = assertRollingEvidence(evidence);
  assert.equal(assertions.length, 11);
  assert.ok(assertions.every(item => item.passed));

  evidence.finalHistory.runs.push({ ...run2 });
  assert.throws(() => assertRollingEvidence(evidence), /no-duplicate-claims/);
});

test('evidence redaction removes disposable credentials', () => {
  const token = 'secret-token-value';
  assert.equal(redact(`Authorization: Bearer ${token}`, [token]), 'Authorization: Bearer [REDACTED]');
});
