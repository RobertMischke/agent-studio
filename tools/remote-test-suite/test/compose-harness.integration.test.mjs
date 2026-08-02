import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { runCommand } from '../core.mjs';

test('real Compose profile uses the short card autonomy window and removes only its identity', {
  skip: process.env.REMOTE_TEST_COMPOSE_INTEGRATION !== '1',
  timeout: 900_000
}, async () => {
  const result = await runComposeAcceptance([]);
  const expectedSeconds = Number(process.env.REMOTE_TEST_AUTONOMY_SECONDS ?? 25);
  assert.equal(result.autonomy.policy.mode, 'short-card');
  assert.equal(result.autonomy.policy.requiredDurationMs, expectedSeconds * 1000);
});

test('MachineBound: real ten-minute Runner autonomy canary', {
  skip: process.env.REMOTE_TEST_COMPOSE_MACHINE_BOUND !== '1',
  timeout: 1_800_000
}, async () => {
  const result = await runComposeAcceptance([
    '--autonomy-duration-seconds', '600',
    '--machine-bound'
  ]);
  assert.equal(result.autonomy.policy.mode, 'machine-bound-ten-minute');
  assert.ok(result.autonomy.durationMs >= 600_000);
});

async function runComposeAcceptance(extraArgs) {
  const suffix = path.basename(await mkdtemp(path.join(os.tmpdir(), 'rts-compose-')))
    .toLowerCase().replace(/[^a-z0-9-]/g, '').slice(-12);
  const runId = `node-${suffix}`;
  const repoRoot = path.resolve(import.meta.dirname, '..', '..', '..');
  const ports = await allocatePortBlock();
  const execution = await runCommand([
    'node', 'tools/remote-test-suite/compose-harness.mjs',
    'run', '--run-id', runId,
    '--task-server-port', String(ports[0]),
    '--studio-port', String(ports[1]),
    '--fault-control-port', String(ports[2]),
    '--runner-control-port', String(ports[3]),
    ...extraArgs
  ], { cwd: repoRoot });
  const result = JSON.parse(execution.stdout);
  assert.equal(result.assertions.every(item => item.passed), true);
  assert.equal(result.rollingTask.finalState, '2-ready');
  const teardown = JSON.parse(await readFile(
    path.join(repoRoot, '.tmp', 'remote-test-suite', 'compose', runId, 'evidence', 'teardown.json'),
    'utf8'
  ));
  assert.deepEqual(teardown.residues, { containers: [], volumes: [], networks: [] });
  return result;
}

async function allocatePortBlock() {
  const servers = [];
  try {
    for (let index = 0; index < 4; index++) {
      const server = net.createServer();
      await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
      });
      servers.push(server);
    }
    return servers.map(server => server.address().port);
  } finally {
    await Promise.all(servers.map(server => new Promise((resolve, reject) =>
      server.close(error => error ? reject(error) : resolve()))));
  }
}
