#!/usr/bin/env node
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { runCommand, sha256 } from './core.mjs';

const inputIndex = process.argv.indexOf('--input');
const inputFile = inputIndex >= 0 ? process.argv[inputIndex + 1] : null;
if (!inputFile) throw new Error('Usage: parallel-executor.mjs --input FILE');
const input = JSON.parse(await readFile(path.resolve(inputFile), 'utf8'));
const startedAt = new Date().toISOString();
const started = performance.now();

if (input.holdMs) await delay(input.holdMs);

let result;
if (input.role === 'coding') result = await executeCoding(input);
else if (input.role === 'gate' || input.role === 'review') result = await executeExactSha(input);
else throw new Error(`Unsupported parallel executor role: ${input.role}`);

const usage = process.resourceUsage();
console.log(JSON.stringify({
  ...result,
  role: input.role,
  workerId: input.workerId,
  hostId: input.hostId,
  slot: input.slot,
  processId: process.pid,
  startedAt,
  finishedAt: new Date().toISOString(),
  durationMs: Math.round((performance.now() - started) * 1000) / 1000,
  cpuUserMicros: usage.userCPUTime,
  cpuSystemMicros: usage.systemCPUTime,
  maxRssBytes: usage.maxRSS * 1024
}));

async function executeCoding(input) {
  const workspace = path.resolve(input.workspace);
  const baseSha = (await runCommand(['git', 'rev-parse', 'HEAD'], { cwd: workspace })).stdout.trim();
  const namespace = `task-${String(input.ordinal).padStart(3, '0')}`;
  const sourceDir = path.join(workspace, 'src', namespace);
  const testDir = path.join(workspace, 'test');
  const docsDir = path.join(workspace, 'docs');
  await mkdir(sourceDir, { recursive: true });
  await mkdir(testDir, { recursive: true });
  await mkdir(docsDir, { recursive: true });

  const cents = 100 + input.ordinal * 7;
  await writeFile(path.join(sourceDir, 'quote.mjs'), [
    `export const taskKey = '${input.taskKey}';`,
    `export const fixtureSeed = '${input.seed}';`,
    '',
    'export function quoteUnits(units) {',
    "  if (!Number.isInteger(units) || units < 1) throw new RangeError('units must be a positive integer');",
    `  return units * ${cents};`,
    '}',
    ''
  ].join('\n'));
  await writeFile(path.join(testDir, `${namespace}.test.mjs`), [
    "import test from 'node:test';",
    "import assert from 'node:assert/strict';",
    `import { fixtureSeed, quoteUnits, taskKey } from '../src/${namespace}/quote.mjs';`,
    '',
    `test('${input.taskKey} has an isolated deterministic quote', () => {`,
    `  assert.equal(taskKey, '${input.taskKey}');`,
    `  assert.equal(fixtureSeed, '${input.seed}');`,
    `  assert.equal(quoteUnits(3), ${cents * 3});`,
    "  assert.throws(() => quoteUnits(0), RangeError);",
    '});',
    ''
  ].join('\n'));
  await writeFile(path.join(docsDir, `${namespace}.md`), [
    `# ${input.taskKey} reference delivery`,
    '',
    `This deterministic fixture belongs only to ${input.taskKey}.`,
    ''
  ].join('\n'));

  const changed = (await runCommand([
    'git', 'status', '--short', '--untracked-files=all'
  ], { cwd: workspace })).stdout
    .trimEnd()
    .split(/\r?\n/)
    .filter(Boolean)
    .map(line => line.slice(3))
    .sort();
  const expected = [
    `docs/${namespace}.md`,
    `src/${namespace}/quote.mjs`,
    `test/${namespace}.test.mjs`
  ];
  if (JSON.stringify(changed) !== JSON.stringify(expected)) {
    throw new Error(`Unexpected ${input.taskKey} change set: ${JSON.stringify(changed)}`);
  }

  const gate = await runCommand(['node', '--test', `test/${namespace}.test.mjs`], { cwd: workspace });
  await runCommand(['git', 'add', '.'], { cwd: workspace });
  await runCommand(['git', 'commit', '-m', `feat(${input.taskKey}): isolated remote reference change`], {
    cwd: workspace,
    env: {
      GIT_AUTHOR_DATE: input.commitDate,
      GIT_COMMITTER_DATE: input.commitDate
    }
  });
  const resultSha = (await runCommand(['git', 'rev-parse', 'HEAD'], { cwd: workspace })).stdout.trim();
  const treeSha = (await runCommand(['git', 'rev-parse', 'HEAD^{tree}'], { cwd: workspace })).stdout.trim();
  const resultRef = `refs/heads/agent-studio/results/${input.runAttemptId}/${resultSha}`;
  await runCommand([
    'git', 'push', 'origin',
    `${input.branch}:${input.branch}`,
    `HEAD:${resultRef}`
  ], { cwd: workspace });
  return {
    taskKey: input.taskKey,
    ordinal: input.ordinal,
    workspace,
    baseSha,
    resultSha,
    resultRef,
    treeSha,
    changedFiles: changed,
    commandOutputSha256: sha256(gate.stdout + gate.stderr)
  };
}

async function executeExactSha(input) {
  const workspace = path.resolve(input.workspace);
  if (input.attemptRoot) {
    for (const directory of ['artifacts', 'cache', 'tmp', 'home']) {
      await mkdir(path.join(path.resolve(input.attemptRoot), directory), { recursive: true });
    }
  }
  await mkdir(path.dirname(workspace), { recursive: true });
  await runCommand(['git', 'clone', input.origin, workspace], { cwd: path.dirname(workspace) });
  await runCommand(['git', 'fetch', 'origin', `${input.resultRef}:${input.resultRef}`], { cwd: workspace });
  await runCommand(['git', 'checkout', '--detach', input.expectedResultSha], { cwd: workspace });
  const actualHead = (await runCommand(['git', 'rev-parse', 'HEAD'], { cwd: workspace })).stdout.trim();
  const treeSha = (await runCommand(['git', 'rev-parse', 'HEAD^{tree}'], { cwd: workspace })).stdout.trim();
  if (actualHead !== input.expectedResultSha) {
    throw new Error(`Exact-SHA ${input.role} mismatch: ${actualHead} != ${input.expectedResultSha}`);
  }
  const commandStartedAt = new Date().toISOString();
  const command = await runCommand(['node', '--test', 'test/*.test.mjs'], { cwd: workspace });
  const commandFinishedAt = new Date().toISOString();
  const dirtyAfter = (await runCommand(['git', 'status', '--porcelain'], { cwd: workspace })).stdout.trim().length > 0;
  if (dirtyAfter) throw new Error(`${input.role} workspace mutated while verifying ${input.taskKey}`);
  return {
    taskKey: input.taskKey,
    ordinal: input.ordinal,
    workspace,
    expectedResultSha: input.expectedResultSha,
    actualHead,
    treeSha,
    dirtyBefore: false,
    dirtyAfter,
    command: {
      fileName: 'node',
      arguments: ['--test', 'test/*.test.mjs'],
      startedAt: commandStartedAt,
      finishedAt: commandFinishedAt,
      exitCode: 0,
      stdoutSha256: sha256(command.stdout),
      stderrSha256: sha256(command.stderr),
      stdoutSizeBytes: Buffer.byteLength(command.stdout),
      stderrSizeBytes: Buffer.byteLength(command.stderr)
    }
  };
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
