import { createHash } from 'node:crypto';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';

export const expectedPhases = Object.freeze(['claim', 'run', 'gate', 'review', 'integration']);

export function validateManifest(manifest) {
  const errors = [];
  rejectUnknown(manifest, [
    '$schema', 'version', 'name', 'description', 'task', 'fixture', 'phases', 'resources', 'hooks'
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
  if (errors.length) throw new Error(`Invalid scenario manifest:\n- ${errors.join('\n- ')}`);
  return manifest;
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

export function resourcePlan({ baseRoot, scenario, runId, serverUrl, ownsServer = true }) {
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
    `API workspace/project/task scoped to ${runId}`
  ];
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

export async function runCommand(command, { cwd, env = {}, capture = true } = {}) {
  if (!Array.isArray(command) || command.length === 0) throw new Error('Command is empty.');
  return await new Promise((resolve, reject) => {
    const child = spawn(command[0], command.slice(1), {
      cwd,
      env: { ...process.env, ...env },
      stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit'
    });
    let stdout = '';
    let stderr = '';
    child.stdout?.on('data', chunk => { stdout += chunk; });
    child.stderr?.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', code => {
      if (code === 0) resolve({ stdout, stderr, code });
      else reject(new Error(`${command.join(' ')} failed (${code}): ${stderr || stdout}`));
    });
  });
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
  const current = await readFile(file, 'utf8').catch(() => '');
  await writeFile(file, `${current}${JSON.stringify(value)}\n`);
}
