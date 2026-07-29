import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  CommandTimeoutError,
  cleanupRunRoot,
  readJson,
  runCommand
} from '../core.mjs';
import {
  assertFaultActivationRequest,
  FaultController,
  faultActivationToken,
  initializeFaultSafetyMarker,
  resolveFaultSelection,
  validateFaultCatalog
} from '../faults.mjs';

const suiteRoot = path.resolve(import.meta.dirname, '..');
const catalog = validateFaultCatalog(
  await readJson(path.join(suiteRoot, 'fault-catalog.json')));

test('catalog covers all required incident classes and stable history anchors', () => {
  const incidentClasses = new Set(catalog.faults.map(fault => fault.incidentClass));
  assert.deepEqual(
    [...incidentClasses].sort(),
    ['gate-timeout', 'task-server-network', 'terminal-marker-loss', 'worktree-collision']);
  for (const fault of catalog.faults) {
    assert.ok(fault.anchors.every(anchor => anchor.includes('historie.html#incident-')));
  }
});

test('every catalog anchor resolves to a unique incident-history id', async () => {
  const historyPath = path.resolve(
    suiteRoot,
    '..',
    '..',
    'docs',
    'operations',
    'haertung-verteilte-ausfuehrung',
    'historie.html');
  const history = await readFile(historyPath, 'utf8');
  const anchors = new Set(catalog.faults.flatMap(fault => fault.anchors));
  for (const anchor of anchors) {
    const id = anchor.split('#').at(-1);
    assert.equal(
      history.match(new RegExp(`id="${id}"`, 'g'))?.length,
      1,
      anchor);
  }
});

test('disabled injection is behaviorally inert and consumes no schedule', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-inert-'));
  const marker = await initializeFaultSafetyMarker({
    root,
    scenario: 'reference-change',
    runId: 'inert',
    selectedFaults: [],
    enabled: false,
    acknowledgement: '',
    ownsServer: true
  });
  const controller = new FaultController({
    root,
    selectedFaults: [],
    marker
  });
  assert.equal(await controller.next('claim'), null);
  assert.equal(await controller.next('gate-command'), null);
  controller.assertConsumed();
  assert.deepEqual(controller.snapshot(), {
    enabled: false,
    selected: [],
    encounters: { claim: 1, 'gate-command': 1 },
    scheduled: 0,
    consumed: 0
  });
  await cleanupRunRoot(root, path.dirname(root));
});

test('activation requires a run-bound acknowledgement and a harness-owned server', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-activation-'));
  const selectedFaults = resolveFaultSelection(catalog, ['task-server-network-blips']);
  const request = {
    root,
    scenario: 'fault-task-server-network-blips',
    runId: 'activation',
    selectedFaults,
    enabled: true,
    acknowledgement: 'wrong',
    ownsServer: true
  };
  assert.throws(() => assertFaultActivationRequest(request), /does not match/);
  const acknowledgement = faultActivationToken(request);
  assert.throws(() => assertFaultActivationRequest({
    ...request,
    acknowledgement,
    ownsServer: false
  }), /requires a harness-owned isolated Task Server/);
  assert.equal(assertFaultActivationRequest({
    ...request,
    acknowledgement
  }), acknowledgement);
  await cleanupRunRoot(root, path.dirname(root));
});

test('safety marker is rechecked before every deterministic fault', async () => {
  const base = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-marker-'));
  const root = path.join(base, 'fault-task-server-network-blips', 'marker');
  await mkdir(root, { recursive: true });
  const selectedFaults = resolveFaultSelection(catalog, ['task-server-network-blips']);
  const acknowledgement = faultActivationToken({
    root,
    scenario: 'fault-task-server-network-blips',
    runId: 'marker'
  });
  const marker = await initializeFaultSafetyMarker({
    root,
    scenario: 'fault-task-server-network-blips',
    runId: 'marker',
    selectedFaults,
    enabled: true,
    acknowledgement,
    ownsServer: true
  });
  const controller = new FaultController({ root, selectedFaults, marker });
  assert.equal((await controller.next('claim')).action, 'disconnect-before-send');
  const log = await readFile(path.join(root, 'faults.jsonl'), 'utf8');
  assert.match(log, /task-server-network-blips/);
  await cleanupRunRoot(root, base);
});

test('conflicting terminal-marker schedules cannot be combined', () => {
  assert.throws(() => resolveFaultSelection(catalog, [
    'lost-completion-sentinel',
    'interrupted-terminal-marker'
  ]), /schedules conflict at terminal-marker:1/);
});

test('watchdog timeout reaps the deterministic fixture process tree', {
  skip: process.platform === 'win32'
}, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'remote-suite-timeout-'));
  const marker = path.join(root, 'pids.json');
  await assert.rejects(() => runCommand([
    process.execPath,
    path.join(suiteRoot, 'fixtures', 'gate-timeout.mjs'),
    '--marker',
    marker,
    '--delay-ms',
    '10000'
  ], {
    cwd: root,
    timeoutMs: 250
  }), error => {
    assert.ok(error instanceof CommandTimeoutError);
    assert.equal(error.classification, 'infrastructure-timeout');
    assert.equal(error.productTestFailure, false);
    return true;
  });
  const pids = JSON.parse(await readFile(marker, 'utf8'));
  const values = [pids.parentPid, pids.childPid];
  for (let attempt = 0; attempt < 40 && values.some(processExists); attempt++) {
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  for (const pid of values) {
    assert.throws(() => process.kill(pid, 0), error => error.code === 'ESRCH');
  }
  await cleanupRunRoot(root, path.dirname(root));
});

function processExists(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error.code === 'ESRCH') return false;
    throw error;
  }
}
