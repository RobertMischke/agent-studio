import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { runCommand } from '../core.mjs';

const scenarios = [
  {
    name: 'divergent-salvage-lineage',
    seed: 'historical-agt-2177',
    terminal: '5-human-review',
    budgetUsed: 3
  },
  {
    name: 'lease-adoption-restart',
    seed: 'historical-agt-2182',
    terminal: '4-auto-review',
    budgetUsed: 1
  },
  {
    name: 'external-completion-cycle',
    seed: 'historical-agt-2155',
    terminal: '2-ready',
    budgetUsed: 1
  }
];

for (const scenario of scenarios) {
  test(`${scenario.name} reaches its declared bounded terminal through isolated contracts`, {
    skip: process.env.REMOTE_TEST_SUITE_INTEGRATION !== '1',
    timeout: 300_000
  }, async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'historical-remote-replay-'));
    const execution = await runCommand([
      'node', 'tools/remote-test-suite/index.mjs',
      '--scenario', scenario.name,
      '--seed', scenario.seed,
      '--run-id', 'regression',
      '--root', root,
      '--cleanup'
    ], { cwd: path.resolve(import.meta.dirname, '..', '..', '..') });
    const result = JSON.parse(execution.stdout);

    assert.equal(result.accepted, true);
    assert.equal(result.expectedTerminal, scenario.terminal);
    assert.equal(result.actualTerminal, scenario.terminal);
    assert.equal(result.recoveryBudget.used, scenario.budgetUsed);
    assert.ok(result.recoveryBudget.used <= result.recoveryBudget.maximum);
    assert.ok(result.assertions.length > 0);
    assert.ok(result.assertions.every(assertion => assertion.passed));
    assert.deepEqual(
      result.phaseSequence,
      ['claim', 'run', 'gate', 'review', 'integration']);
  });
}
