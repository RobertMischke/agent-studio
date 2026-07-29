import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { appendJsonl } from './core.mjs';

const markerName = '.fault-injection-safety.json';
const supportedActions = new Set([
  'disconnect-before-send',
  'disconnect-after-commit',
  'watchdog-timeout',
  'occupy-target',
  'drop-sentinel',
  'interrupt-marker'
]);

export class InjectedNetworkFault extends Error {
  constructor(operation, action, occurrence) {
    super(`Injected Task Server network blip at ${operation} occurrence ${occurrence} (${action}).`);
    this.name = 'InjectedNetworkFault';
    this.operation = operation;
    this.action = action;
    this.occurrence = occurrence;
    this.transient = true;
  }
}

export function validateFaultCatalog(catalog) {
  const errors = [];
  rejectUnknown(catalog, ['version', 'harness', 'faults'], 'catalog', errors);
  if (catalog?.version !== 1) errors.push('catalog.version must be 1');
  if (catalog?.harness !== 'remote-test-suite') {
    errors.push('catalog.harness must be remote-test-suite');
  }
  if (!Array.isArray(catalog?.faults) || catalog.faults.length === 0) {
    errors.push('catalog.faults must be a non-empty array');
  }
  const ids = new Set();
  for (const [index, fault] of (catalog?.faults ?? []).entries()) {
    const label = `faults[${index}]`;
    rejectUnknown(fault, [
      'id', 'incidentClass', 'description', 'anchors', 'schedule'
    ], label, errors);
    if (!/^[a-z0-9][a-z0-9-]{1,63}$/.test(fault?.id ?? '')) {
      errors.push(`${label}.id is invalid`);
    } else if (ids.has(fault.id)) {
      errors.push(`${label}.id is duplicated`);
    } else {
      ids.add(fault.id);
    }
    if (!fault?.incidentClass?.trim()) errors.push(`${label}.incidentClass is required`);
    if (!Array.isArray(fault?.anchors) || fault.anchors.length === 0
        || fault.anchors.some(anchor =>
          typeof anchor !== 'string'
          || !anchor.startsWith('docs/operations/haertung-verteilte-ausfuehrung/historie.html#incident-'))) {
      errors.push(`${label}.anchors must contain stable incident history anchors`);
    }
    if (!Array.isArray(fault?.schedule) || fault.schedule.length === 0) {
      errors.push(`${label}.schedule must be a non-empty array`);
    }
    for (const [scheduleIndex, step] of (fault?.schedule ?? []).entries()) {
      const stepLabel = `${label}.schedule[${scheduleIndex}]`;
      rejectUnknown(step, [
        'operation', 'occurrences', 'action', 'parameters'
      ], stepLabel, errors);
      if (!/^[a-z][a-z0-9-]+$/.test(step?.operation ?? '')) {
        errors.push(`${stepLabel}.operation is invalid`);
      }
      if (!supportedActions.has(step?.action)) {
        errors.push(`${stepLabel}.action is unsupported`);
      }
      if (!Array.isArray(step?.occurrences) || step.occurrences.length === 0
          || new Set(step.occurrences).size !== step.occurrences.length
          || step.occurrences.some(value => !Number.isInteger(value) || value < 1)) {
        errors.push(`${stepLabel}.occurrences must contain unique positive integers`);
      }
      if (step?.parameters !== undefined
          && (!step.parameters || typeof step.parameters !== 'object' || Array.isArray(step.parameters))) {
        errors.push(`${stepLabel}.parameters must be an object`);
      }
    }
  }
  if (errors.length) throw new Error(`Invalid fault catalog:\n- ${errors.join('\n- ')}`);
  return catalog;
}

function rejectUnknown(value, allowed, label, errors) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return;
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) errors.push(`${label}.${key} is not supported`);
  }
}

export function faultActivationToken({ scenario, runId, root }) {
  const input = [
    'remote-test-suite-fault-injection-v1',
    path.resolve(root),
    scenario,
    runId
  ].join('\0');
  return `rts-fi-${createHash('sha256').update(input).digest('hex')}`;
}

export function resolveFaultSelection(catalog, selectedIds) {
  const byId = new Map(catalog.faults.map(fault => [fault.id, fault]));
  const selected = [];
  for (const id of selectedIds ?? []) {
    const fault = byId.get(id);
    if (!fault) throw new Error(`Unknown fault catalog id '${id}'.`);
    selected.push(fault);
  }
  const occupied = new Map();
  for (const fault of selected) {
    for (const step of fault.schedule) {
      for (const occurrence of step.occurrences) {
        const key = `${step.operation}:${occurrence}`;
        const prior = occupied.get(key);
        if (prior) {
          throw new Error(
            `Fault schedules conflict at ${key}: '${prior}' and '${fault.id}'.`);
        }
        occupied.set(key, fault.id);
      }
    }
  }
  return selected;
}

export async function initializeFaultSafetyMarker({
  root,
  scenario,
  runId,
  selectedFaults,
  enabled,
  acknowledgement,
  ownsServer
}) {
  const expected = assertFaultActivationRequest({
    root,
    scenario,
    runId,
    selectedFaults,
    enabled,
    acknowledgement,
    ownsServer
  });
  if (selectedFaults.length === 0) return null;
  const marker = {
    harness: 'remote-test-suite',
    version: 1,
    scenario,
    runId,
    root: path.resolve(root),
    acknowledgement: expected,
    faults: selectedFaults.map(fault => fault.id).sort()
  };
  await writeFile(path.join(root, markerName), `${JSON.stringify(marker, null, 2)}\n`, {
    flag: 'wx'
  });
  return marker;
}

export function assertFaultActivationRequest({
  root,
  scenario,
  runId,
  selectedFaults,
  enabled,
  acknowledgement,
  ownsServer
}) {
  if (selectedFaults.length === 0) {
    if (enabled || acknowledgement) {
      throw new Error('Fault activation flags were supplied to a scenario with no faults.');
    }
    return null;
  }
  if (!enabled) {
    throw new Error('Fault manifest is inert unless --enable-faults is supplied.');
  }
  if (!ownsServer) {
    throw new Error('Fault injection refuses --server-url and requires a harness-owned isolated Task Server.');
  }
  const expected = faultActivationToken({ scenario, runId, root });
  if (acknowledgement !== expected) {
    throw new Error(
      'Fault acknowledgement does not match this scenario, run id, and isolated root. Run --dry-run to obtain it.');
  }
  return expected;
}

export class FaultController {
  constructor({ root, selectedFaults, marker }) {
    this.root = root;
    this.selectedFaults = selectedFaults;
    this.marker = marker;
    this.encounters = new Map();
    this.consumed = new Set();
    this.logFile = path.join(root, 'faults.jsonl');
    this.steps = new Map();
    for (const fault of selectedFaults) {
      for (const step of fault.schedule) {
        for (const occurrence of step.occurrences) {
          this.steps.set(`${step.operation}:${occurrence}`, {
            ...step,
            faultId: fault.id,
            incidentClass: fault.incidentClass,
            occurrence
          });
        }
      }
    }
  }

  get active() {
    return this.selectedFaults.length > 0;
  }

  get ids() {
    return this.selectedFaults.map(fault => fault.id);
  }

  has(id) {
    return this.selectedFaults.some(fault => fault.id === id);
  }

  async next(operation) {
    const occurrence = (this.encounters.get(operation) ?? 0) + 1;
    this.encounters.set(operation, occurrence);
    const key = `${operation}:${occurrence}`;
    const step = this.steps.get(key);
    if (!step) return null;
    await this.assertArmed();
    this.consumed.add(key);
    await appendJsonl(this.logFile, {
      faultId: step.faultId,
      incidentClass: step.incidentClass,
      operation,
      occurrence,
      action: step.action,
      parameters: step.parameters ?? {}
    });
    return step;
  }

  async assertArmed() {
    if (!this.active || !this.marker) {
      throw new Error('Fault controller is not armed.');
    }
    const persisted = JSON.parse(
      await readFile(path.join(this.root, markerName), 'utf8'));
    if (JSON.stringify(persisted) !== JSON.stringify(this.marker)) {
      throw new Error('Fault safety marker changed after activation.');
    }
  }

  assertConsumed() {
    const missing = [...this.steps.keys()].filter(key => !this.consumed.has(key));
    if (missing.length) {
      throw new Error(`Fault schedule was not fully consumed: ${missing.join(', ')}`);
    }
  }

  snapshot() {
    return {
      enabled: this.active,
      selected: [...this.ids],
      encounters: Object.fromEntries(this.encounters),
      scheduled: this.steps.size,
      consumed: this.consumed.size
    };
  }
}
