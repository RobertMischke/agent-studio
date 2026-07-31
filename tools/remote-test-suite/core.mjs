import { createHash } from 'node:crypto';
import { access, mkdir, open, readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';

export const expectedPhases = Object.freeze(['claim', 'run', 'gate', 'review', 'integration']);

export function validateManifest(manifest) {
  const errors = [];
  rejectUnknown(manifest, [
    '$schema', 'version', 'name', 'description', 'task', 'fixture', 'phases', 'resources', 'hooks',
    'acceptance', 'contract', 'faults', 'expected'
  ], 'manifest', errors);
  if (manifest?.version !== 1) errors.push('version must be 1');
  if (!/^[a-z0-9][a-z0-9-]{1,63}$/.test(manifest?.name ?? '')) errors.push('name is invalid');
  rejectUnknown(manifest?.task, ['title', 'body', 'key'], 'task', errors);
  if (!manifest?.task?.title?.trim()) errors.push('task.title is required');
  if (!manifest?.task?.body?.trim()) errors.push('task.body is required');
  if (!/^[A-Z][A-Z0-9-]*-[0-9]+$/.test(manifest?.task?.key ?? '')) errors.push('task.key is invalid');
  if (JSON.stringify(manifest?.phases) !== JSON.stringify(expectedPhases)) {
    errors.push(`phases must be exactly ${expectedPhases.join(', ')}`);
  }
  for (const field of ['workspaceId', 'projectId', 'taskId']) {
    if (!manifest?.resources?.[field]?.trim()) errors.push(`resources.${field} is required`);
  }
  rejectUnknown(manifest?.resources, ['workspaceId', 'projectId', 'taskId'], 'resources', errors);
  if (manifest?.acceptance !== undefined && manifest?.contract !== undefined) {
    errors.push('manifest must not declare both acceptance and contract');
  }
  validateScenarioContract(manifest?.acceptance, 'acceptance', errors);
  validateScenarioContract(manifest?.contract, 'contract', errors);
  rejectUnknown(manifest?.fixture, [
    'defaultBranch', 'changeCommand', 'acceptanceCommand', 'expectedChangedFiles'
  ], 'fixture', errors);
  if (!/^[A-Za-z0-9._/-]+$/.test(manifest?.fixture?.defaultBranch ?? '')
      || manifest.fixture.defaultBranch.startsWith('-')
      || manifest.fixture.defaultBranch.includes('..')) {
    errors.push('fixture.defaultBranch is invalid');
  }
  for (const field of ['changeCommand', 'acceptanceCommand']) {
    const command = manifest?.fixture?.[field];
    if (!Array.isArray(command) || command.length === 0
        || command.some(value => typeof value !== 'string' || value.length === 0)) {
      errors.push(`fixture.${field} must be a non-empty command`);
    }
  }
  const files = manifest?.fixture?.expectedChangedFiles;
  if (!Array.isArray(files) || files.length < 3 || new Set(files).size !== files.length) {
    errors.push('fixture.expectedChangedFiles must contain at least three unique paths');
  } else if (files.some(file => !safeRelativePath(file))) {
    errors.push('fixture.expectedChangedFiles must contain safe relative paths');
  }
  rejectUnknown(manifest?.hooks, expectedPhases, 'hooks', errors);
  for (const [phase, command] of Object.entries(manifest?.hooks ?? {})) {
    if (!expectedPhases.includes(phase)) errors.push(`hooks.${phase} is not a supported phase`);
    if (!Array.isArray(command) || command.length === 0
        || command.some(value => typeof value !== 'string' || value.length === 0)) {
      errors.push(`hooks.${phase} must be a non-empty command`);
    }
  }
  if (manifest?.faults !== undefined
      && (!Array.isArray(manifest.faults)
          || new Set(manifest.faults).size !== manifest.faults.length
          || manifest.faults.some(value => !/^[a-z0-9][a-z0-9-]{1,63}$/.test(value)))) {
    errors.push('faults must contain unique fault catalog ids');
  }
  if (manifest?.expected !== undefined) {
    rejectUnknown(manifest.expected, [
      'accepted', 'finalLane', 'incidentOutcome', 'phaseSequence'
    ], 'expected', errors);
    if (typeof manifest.expected.accepted !== 'boolean') {
      errors.push('expected.accepted must be a boolean');
    }
    if (!manifest.expected.finalLane?.trim()) errors.push('expected.finalLane is required');
    if (!manifest.expected.incidentOutcome?.trim()) errors.push('expected.incidentOutcome is required');
    if (!Array.isArray(manifest.expected.phaseSequence)
        || manifest.expected.phaseSequence.some(phase => !expectedPhases.includes(phase))) {
      errors.push('expected.phaseSequence must contain supported phases');
    }
  }
  if (errors.length) throw new Error(`Invalid scenario manifest:\n- ${errors.join('\n- ')}`);
  return manifest;
}

function validateScenarioContract(contract, label, errors) {
  if (contract === undefined) return;
  rejectUnknown(
    contract,
    ['chronicleLinks', 'expectedTerminal', 'recoveryBudget', 'assertions'],
    label,
    errors);
  const chronicleLinks = contract?.chronicleLinks;
  if (!Array.isArray(chronicleLinks)
      || new Set(chronicleLinks).size !== chronicleLinks.length
      || chronicleLinks.some(link =>
        !/^docs\/operations\/haertung-verteilte-ausfuehrung\/historie\.html#incident-[a-z0-9-]+$/.test(link))) {
    errors.push(`${label}.chronicleLinks must contain unique hardening-chronicle incident links`);
  }
  if (!/^[0-9]+-[a-z0-9-]+$/.test(contract?.expectedTerminal ?? '')) {
    errors.push(`${label}.expectedTerminal must be a durable lane state`);
  }
  rejectUnknown(
    contract?.recoveryBudget,
    ['unit', 'maximum'],
    `${label}.recoveryBudget`,
    errors);
  if (!/^[a-z0-9][a-z0-9-]+$/.test(contract?.recoveryBudget?.unit ?? '')
      || !Number.isInteger(contract?.recoveryBudget?.maximum)
      || contract.recoveryBudget.maximum < 0) {
    errors.push(`${label}.recoveryBudget must declare a unit and non-negative maximum`);
  }
  const assertions = contract?.assertions;
  if (!Array.isArray(assertions)
      || assertions.length === 0
      || new Set(assertions).size !== assertions.length
      || assertions.some(assertion => !/^[a-z0-9][a-z0-9-]+$/.test(assertion))) {
    errors.push(`${label}.assertions must contain unique machine assertion ids`);
  }
}

export function scenarioAssertions(manifest) {
  const contract = manifest.contract ?? manifest.acceptance;
  if (!contract) throw new Error('Scenario assertions require acceptance or contract metadata.');
  const declared = new Set(contract.assertions);
  const observed = new Map();
  return {
    check(id, condition, detail) {
      if (!declared.has(id)) throw new Error(`Scenario used undeclared assertion '${id}'.`);
      if (observed.has(id)) throw new Error(`Scenario assertion '${id}' was recorded twice.`);
      if (!condition) throw new Error(`Scenario assertion '${id}' failed: ${detail}`);
      observed.set(id, { id, passed: true, detail });
    },
    finish(actualTerminal, recoveryUsed) {
      const missing = [...declared].filter(id => !observed.has(id));
      if (missing.length > 0) {
        throw new Error(`Scenario did not execute declared assertions: ${missing.join(', ')}`);
      }
      if (actualTerminal !== contract.expectedTerminal) {
        throw new Error(
          `Scenario terminal mismatch: expected ${contract.expectedTerminal}, got ${actualTerminal}`);
      }
      if (!Number.isInteger(recoveryUsed)
          || recoveryUsed < 0
          || recoveryUsed > contract.recoveryBudget.maximum) {
        throw new Error(
          `Scenario recovery budget exceeded: ${recoveryUsed}/${contract.recoveryBudget.maximum} `
          + contract.recoveryBudget.unit);
      }
      return {
        expectedTerminal: contract.expectedTerminal,
        actualTerminal,
        recoveryBudget: {
          ...contract.recoveryBudget,
          used: recoveryUsed
        },
        chronicleLinks: [...contract.chronicleLinks],
        assertions: [...observed.values()]
      };
    }
  };
}

function rejectUnknown(value, allowed, label, errors) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return;
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) errors.push(`${label}.${key} is not supported`);
  }
}

function safeRelativePath(value) {
  return typeof value === 'string'
         && value.length > 0
         && !path.isAbsolute(value)
         && !value.split(/[\\/]/).includes('..');
}

export function interpolate(value, variables) {
  return value.replace(/\{([A-Za-z][A-Za-z0-9]*)\}/g, (_, key) => {
    if (!(key in variables)) throw new Error(`Unknown manifest variable {${key}}`);
    return variables[key];
  });
}

export function resourcePlan({
  baseRoot,
  scenario,
  runId,
  serverUrl,
  ownsServer = true,
  faults = []
}) {
  const root = path.resolve(baseRoot, scenario, runId);
  assertScopedRoot(root, baseRoot);
  const creates = [
    `${root}/fixture-origin.git`,
    `${root}/fixture-seed`,
    `${root}/coding`,
    `${root}/review`,
    `${root}/integration`,
    `${root}/outbox.jsonl`,
    `${root}/phases.jsonl`,
    `${root}/assertions.json`,
    `${root}/result.json`,
    `API workspace/project/task scoped to ${runId}`
  ];
  if (faults.length > 0) {
    creates.push(
      `${root}/.fault-injection-safety.json`,
      `${root}/faults.jsonl`);
  }
  if (faults.includes('gate-watchdog-timeout')) {
    creates.push(`${root}/gate-timeout-processes.json`);
  }
  if (faults.includes('worktree-target-collision')) {
    creates.push(`${root}/coding/worktree (fixture-owned foreign Git worktree)`);
  }
  if (ownsServer) creates.push(`${root}/task-server-data`, `isolated Task Server at ${serverUrl}`);
  return {
    root,
    creates,
    destroys: [...creates].reverse(),
    neverTouches: ['agent-taskboard-stable/', 'agent-taskboard-workspace/projects/', 'agent-taskboard-workspace/.metadata/']
  };
}

export function assertScopedRoot(target, baseRoot) {
  const resolvedBase = path.resolve(baseRoot);
  const resolved = path.resolve(target);
  const relative = path.relative(resolvedBase, resolved);
  if (resolvedBase === path.parse(resolvedBase).root || relative.startsWith('..') || path.isAbsolute(relative) || relative === '') {
    throw new Error(`Refusing unscoped resource root: ${resolved}`);
  }
  if (/(^|[/\\])agent-taskboard-stable([/\\]|$)/.test(resolved)
      || /agent-taskboard-workspace[/\\](projects|\\.metadata)([/\\]|$)/.test(resolved)) {
    throw new Error(`Refusing protected resource root: ${resolved}`);
  }
}

export async function resetRunRoot(root, baseRoot) {
  assertScopedRoot(root, baseRoot);
  await rm(root, { recursive: true, force: true });
  await mkdir(root, { recursive: true });
}

export async function cleanupRunRoot(root, baseRoot) {
  assertScopedRoot(root, baseRoot);
  await rm(root, { recursive: true, force: true });
}

export async function setupWithRollback(steps, cleanup) {
  try {
    for (const step of steps) await step();
  } catch (error) {
    await cleanup();
    throw error;
  }
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

export class CommandTimeoutError extends Error {
  constructor(command, timeoutMs, pid, stdout, stderr, signal) {
    super(`${command.join(' ')} exceeded the ${timeoutMs} ms watchdog`);
    this.name = 'CommandTimeoutError';
    this.command = [...command];
    this.timeoutMs = timeoutMs;
    this.pid = pid;
    this.stdout = stdout;
    this.stderr = stderr;
    this.signal = signal;
    this.classification = 'infrastructure-timeout';
    this.productTestFailure = false;
  }
}

export async function runCommand(command, {
  cwd,
  env = {},
  capture = true,
  timeoutMs
} = {}) {
  if (!Array.isArray(command) || command.length === 0) throw new Error('Command is empty.');
  if (timeoutMs !== undefined && (!Number.isInteger(timeoutMs) || timeoutMs < 1)) {
    throw new Error('timeoutMs must be a positive integer.');
  }
  return await new Promise((resolve, reject) => {
    const ownsProcessGroup = timeoutMs !== undefined && process.platform !== 'win32';
    const child = spawn(command[0], command.slice(1), {
      cwd,
      env: { ...process.env, ...env },
      stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
      detached: ownsProcessGroup
    });
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    let forceKillTimer;
    const watchdog = timeoutMs === undefined
      ? undefined
      : setTimeout(() => {
          timedOut = true;
          terminateProcessTree(child, ownsProcessGroup, 'SIGTERM');
          forceKillTimer = setTimeout(
            () => terminateProcessTree(child, ownsProcessGroup, 'SIGKILL'),
            250);
          forceKillTimer.unref?.();
        }, timeoutMs);
    watchdog?.unref?.();
    child.stdout?.on('data', chunk => { stdout += chunk; });
    child.stderr?.on('data', chunk => { stderr += chunk; });
    child.on('error', error => {
      clearTimeout(watchdog);
      clearTimeout(forceKillTimer);
      reject(error);
    });
    child.on('close', (code, signal) => {
      clearTimeout(watchdog);
      clearTimeout(forceKillTimer);
      if (timedOut) {
        reject(new CommandTimeoutError(
          command,
          timeoutMs,
          child.pid,
          stdout,
          stderr,
          signal));
      } else if (code === 0) {
        resolve({ stdout, stderr, code, signal, pid: child.pid });
      } else {
        const error = new Error(`${command.join(' ')} failed (${code ?? signal}): ${stderr || stdout}`);
        error.code = code;
        error.signal = signal;
        error.pid = child.pid;
        error.stdout = stdout;
        error.stderr = stderr;
        reject(error);
      }
    });
  });
}

function terminateProcessTree(child, ownsProcessGroup, signal) {
  if (!child.pid) return;
  try {
    if (process.platform === 'win32') {
      spawn('taskkill', ['/pid', String(child.pid), '/t', '/f'], {
        detached: false,
        stdio: 'ignore',
        windowsHide: true
      }).unref();
    } else if (ownsProcessGroup) {
      process.kill(-child.pid, signal);
    } else {
      child.kill(signal);
    }
  } catch (error) {
    if (error?.code !== 'ESRCH') throw error;
  }
}

export async function readJson(file) {
  return JSON.parse(await readFile(file, 'utf8'));
}

export async function exists(file) {
  try {
    await access(file);
    return true;
  } catch {
    return false;
  }
}

export async function appendJsonl(file, value) {
  const handle = await open(file, 'a');
  try {
    await handle.writeFile(`${JSON.stringify(value)}\n`);
    await handle.sync();
  } finally {
    await handle.close();
  }
}
