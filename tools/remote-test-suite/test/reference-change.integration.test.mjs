import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { runCommand } from '../core.mjs';

test('two clean executions from one seed have equivalent accepted trees and phases', {
  skip: process.env.REMOTE_TEST_SUITE_INTEGRATION !== '1',
  timeout: 300_000
}, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-integration-'));
  const results = [];
  for (const runId of ['clean-a', 'clean-b']) {
    const execution = await runCommand([
      'node', 'tools/remote-test-suite/index.mjs',
      '--scenario', 'reference-change',
      '--seed', 'acceptance-seed-1',
      '--run-id', runId,
      '--root', root,
      '--cleanup'
    ], { cwd: path.resolve(import.meta.dirname, '..', '..', '..') });
    results.push(JSON.parse(execution.stdout));
  }
  assert.equal(results[0].accepted, true);
  assert.equal(results[1].accepted, true);
  assert.equal(results[0].semanticTree, results[1].semanticTree);
  assert.deepEqual(results[0].phaseSequence, ['claim', 'run', 'gate', 'review', 'integration']);
  assert.deepEqual(results[1].phaseSequence, results[0].phaseSequence);
});
