import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { runCommand } from '../core.mjs';

test('real Compose profile rotates all three units and removes only its identity', {
  skip: process.env.REMOTE_TEST_COMPOSE_INTEGRATION !== '1',
  timeout: 1_800_000
}, async () => {
  const suffix = path.basename(await mkdtemp(path.join(os.tmpdir(), 'rts-compose-')))
    .toLowerCase().replace(/[^a-z0-9-]/g, '').slice(-12);
  const runId = `node-${suffix}`;
  const repoRoot = path.resolve(import.meta.dirname, '..', '..', '..');
  const execution = await runCommand([
    'node', 'tools/remote-test-suite/compose-harness.mjs',
    'run', '--run-id', runId
  ], { cwd: repoRoot });
  const result = JSON.parse(execution.stdout);
  assert.equal(result.assertions.every(item => item.passed), true);
  assert.equal(result.rollingTask.finalState, '2-ready');
  const teardown = JSON.parse(await readFile(
    path.join(repoRoot, '.tmp', 'remote-test-suite', 'compose', runId, 'evidence', 'teardown.json'),
    'utf8'
  ));
  assert.deepEqual(teardown.residues, { containers: [], volumes: [], networks: [] });
});
