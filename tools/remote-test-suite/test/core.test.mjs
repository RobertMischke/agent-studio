import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  cleanupRunRoot,
  expectedPhases,
  resourcePlan,
  resetRunRoot,
  scenarioAssertions,
  setupWithRollback,
  validateManifest
} from '../core.mjs';

const valid = {
  version: 1,
  name: 'reference-change',
  task: { title: 'Task', body: 'Body', key: 'RTS-1' },
  fixture: {
    defaultBranch: 'develop',
    changeCommand: ['node', 'change.mjs'],
    acceptanceCommand: ['node', '--test'],
    expectedChangedFiles: ['a', 'b', 'c']
  },
  phases: [...expectedPhases],
  resources: { workspaceId: 'w', projectId: 'p', taskId: 't' },
  contract: {
    chronicleLinks: [],
    expectedTerminal: '5-human-review',
    recoveryBudget: { unit: 'review-attempts', maximum: 2 },
    assertions: ['exact-subject-reviewed']
  },
  hooks: {}
};

test('manifest validation accepts the canonical phase contract', () => {
  assert.equal(validateManifest(structuredClone(valid)).name, 'reference-change');
});

test('manifest validation rejects reordered phases and underspecified fixtures', () => {
  assert.throws(() => validateManifest({
    ...structuredClone(valid),
    phases: ['run', 'claim', 'gate', 'review', 'integration'],
    fixture: { ...valid.fixture, expectedChangedFiles: ['a'] }
  }), /phases must be exactly[\s\S]*at least three/);
});

test('manifest validation rejects unknown fields and traversal paths', () => {
  const manifest = structuredClone(valid);
  manifest.parallelism = 8;
  manifest.fixture.expectedChangedFiles = ['a', 'b', '../outside'];
  assert.throws(() => validateManifest(manifest), /manifest.parallelism[\s\S]*safe relative paths/);
});

test('scenario assertions enforce the declared terminal, budget, and complete assertion set', () => {
  const manifest = validateManifest(structuredClone(valid));
  const assertions = scenarioAssertions(manifest);
  assertions.check('exact-subject-reviewed', true, 'immutable subject accepted');
  const result = assertions.finish('5-human-review', 2);
  assert.equal(result.actualTerminal, '5-human-review');
  assert.equal(result.recoveryBudget.used, 2);
  assert.deepEqual(result.assertions.map(item => item.id), ['exact-subject-reviewed']);

  const missing = scenarioAssertions(manifest);
  assert.throws(() => missing.finish('5-human-review', 0), /did not execute declared assertions/);
  const overBudget = scenarioAssertions(manifest);
  overBudget.check('exact-subject-reviewed', true, 'immutable subject accepted');
  assert.throws(() => overBudget.finish('5-human-review', 3), /recovery budget exceeded/);
});

test('dry-run resource plan explains scoped creates and destroys', () => {
  const plan = resourcePlan({
    baseRoot: '/tmp/remote-suite',
    scenario: 'reference-change',
    runId: 'run-1',
    serverUrl: 'http://127.0.0.1:5071'
  });
  assert.match(plan.root, /reference-change[/\\]run-1$/);
  assert.ok(plan.creates.some(value => value.includes('fixture-origin.git')));
  assert.ok(plan.creates.some(value => value.includes('Task Server')));
  assert.equal(plan.creates.length, plan.destroys.length);
  assert.ok(plan.neverTouches.includes('agent-taskboard-stable/'));
});

test('seeded reset and cleanup are idempotent across repeated runs', async () => {
  const base = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-core-'));
  const root = path.join(base, 'reference-change', 'repeat');
  await resetRunRoot(root, base);
  await resetRunRoot(root, base);
  await cleanupRunRoot(root, base);
  await cleanupRunRoot(root, base);
});

test('partial setup failure invokes cleanup and leaves no run root', async () => {
  const base = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-failure-'));
  const root = path.join(base, 'reference-change', 'failure');
  let cleaned = false;
  await assert.rejects(() => setupWithRollback([
    async () => await resetRunRoot(root, base),
    async () => { throw new Error('injected setup failure'); }
  ], async () => {
    cleaned = true;
    await cleanupRunRoot(root, base);
  }), /injected setup failure/);
  assert.equal(cleaned, true);
  await assert.rejects(readFile(root), /ENOENT|EISDIR/);
});

test('protected stable and managed task roots are rejected', () => {
  assert.throws(() => resourcePlan({
    baseRoot: '/tmp/agent-taskboard-stable',
    scenario: 'reference-change',
    runId: 'run-1',
    serverUrl: 'http://127.0.0.1:5071'
  }), /protected resource root/);
  assert.throws(() => resourcePlan({
    baseRoot: '/tmp/agent-taskboard-workspace/projects',
    scenario: 'reference-change',
    runId: 'run-1',
    serverUrl: 'http://127.0.0.1:5071'
  }), /protected resource root/);
});
