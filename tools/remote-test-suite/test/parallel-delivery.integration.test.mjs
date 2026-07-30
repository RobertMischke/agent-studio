import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { runCommand } from '../core.mjs';

test('twelve-task baseline and worker-loss scenarios satisfy parallel delivery invariants', {
  skip: process.env.REMOTE_TEST_PARALLEL_INTEGRATION !== '1',
  timeout: 900_000
}, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'remote-parallel-integration-'));
  const execution = await runCommand([
    'node',
    'tools/remote-test-suite/parallel-harness.mjs',
    '--run-id',
    'integration-01',
    '--seed',
    'parallel-integration-v1',
    '--root',
    root
  ], { cwd: path.resolve(import.meta.dirname, '..', '..', '..') });
  const result = JSON.parse(execution.stdout);
  assert.equal(result.accepted, true);
  assert.deepEqual(result.scenarios.map(item => item.tasks), [12, 12]);
  assert.equal(result.scenarios[0].peakActiveOrPost, 12);
  assert.ok(result.scenarios[1].retries > 0);
});
