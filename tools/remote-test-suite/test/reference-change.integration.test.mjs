import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { runCommand } from '../core.mjs';
import { faultActivationToken } from '../faults.mjs';

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

test('single-fault and selected multi-fault manifests reach their declared terminals', {
  skip: process.env.REMOTE_TEST_SUITE_INTEGRATION !== '1',
  timeout: 600_000
}, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-fault-integration-'));
  const scenarios = [
    ['fault-task-server-network-blips', true, 'network-blips-replayed'],
    ['fault-gate-timeout', true, 'gate-timeout-recovered'],
    ['fault-worktree-collision', false, 'worktree-blocked'],
    ['fault-lost-completion-sentinel', true, 'lost-terminal-marker-recovered'],
    ['fault-interrupted-terminal-marker', false, 'protocol-inconclusive-human-terminal'],
    [
      'fault-network-and-terminal',
      true,
      'network-blips-replayed+lost-terminal-marker-recovered'
    ],
    [
      'fault-network-and-gate-timeout',
      true,
      'network-blips-replayed+gate-timeout-recovered'
    ]
  ];
  for (const [scenario, accepted, incidentOutcome] of scenarios) {
    const runId = scenario.replace(/^fault-/, '');
    const scenarioRoot = path.join(root, scenario, runId);
    const acknowledgement = faultActivationToken({
      scenario,
      runId,
      root: scenarioRoot
    });
    const execution = await runCommand([
      'node',
      'tools/remote-test-suite/index.mjs',
      '--scenario',
      scenario,
      '--seed',
      'fault-acceptance-seed-1',
      '--run-id',
      runId,
      '--root',
      root,
      '--enable-faults',
      '--fault-ack',
      acknowledgement
    ], {
      cwd: path.resolve(import.meta.dirname, '..', '..', '..')
    });
    const result = JSON.parse(execution.stdout);
    assert.equal(result.accepted, accepted, scenario);
    assert.equal(result.incidentOutcome, incidentOutcome, scenario);
    assert.equal(result.assertions.lease.noDuplicateClaim, true, scenario);
    assert.equal(result.assertions.process.allReaped, true, scenario);
    assert.equal(result.assertions.outbox.backlog, 0, scenario);
    assert.equal(result.assertions.sha.phantomSuccessPrevented, true, scenario);
  }
});
