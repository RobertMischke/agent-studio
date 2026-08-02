#!/usr/bin/env node
// Fake coding-agent CLI for the CAR parity suite (AGT-2372 / T3, Ebene 1).
//
// It replays a recorded fixture instead of calling a model, so the whole
// classification path - stream framing, sentinel scan, outcome adapter, lane
// mapping - can be exercised deterministically against the legacy execution
// layer and, later, against the CodingAgentRunner path, using the exact same
// bytes. See README.md for the fixture grammar.
//
// Usage:
//   node fake-cli.mjs <fixture-path> [ignored CLI args...]
//   FAKE_CLI_FIXTURE=<fixture-path> node fake-cli.mjs [ignored CLI args...]
//
// Environment:
//   FAKE_CLI_DELAY_MS   milliseconds to wait before every replayed line (default 0)
//   FAKE_CLI_EXIT_CODE  override the fixture's exit code (integer)
//   FAKE_CLI_CAPTURE    path to write a JSON record of argv/cwd/stdin for
//                       transport assertions (prompt on stdin vs. argv, size)
//   FAKE_CLI_NO_STDIN   set to 1 to skip reading stdin (fixtures that model an
//                       argv-transport CLI)

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const argv = process.argv.slice(2);
const fixturePath = process.env.FAKE_CLI_FIXTURE ?? argv[0];

function fail(message) {
  process.stderr.write(`fake-cli: ${message}\n`);
  process.exit(64); // EX_USAGE - never collides with a fixture exit code
}

if (!fixturePath) fail('no fixture given (argv[1] or FAKE_CLI_FIXTURE)');
if (!fs.existsSync(fixturePath)) fail(`fixture not found: ${fixturePath}`);

const text = fs.readFileSync(fixturePath, 'utf8');
const lines = text.split(/\r?\n/);

let meta = null;
const steps = [];
let pendingDelay = 0;

for (const line of lines) {
  if (line.trim() === '') continue;
  if (line.startsWith('#!')) {
    if (meta) fail('more than one metadata line');
    try {
      meta = JSON.parse(line.slice(2).trim());
    } catch (error) {
      fail(`metadata line is not valid JSON: ${error.message}`);
    }
    continue;
  }
  if (line.startsWith('#')) continue;
  if (!meta) fail('the first non-comment line must be the "#!" metadata line');
  if (line.startsWith('@delay ')) {
    const ms = Number.parseInt(line.slice(7).trim(), 10);
    if (!Number.isFinite(ms) || ms < 0) fail(`invalid @delay: ${line}`);
    pendingDelay += ms;
    continue;
  }
  if (line.startsWith('!stderr ')) {
    steps.push({ stream: 'stderr', text: line.slice(8), delay: pendingDelay });
    pendingDelay = 0;
    continue;
  }
  steps.push({ stream: 'stdout', text: line, delay: pendingDelay });
  pendingDelay = 0;
}

if (!meta) fail('fixture has no "#!" metadata line');

const perLineDelay = Number.parseInt(process.env.FAKE_CLI_DELAY_MS ?? '0', 10) || 0;
const exitCode = Number.isFinite(Number.parseInt(process.env.FAKE_CLI_EXIT_CODE ?? '', 10))
  ? Number.parseInt(process.env.FAKE_CLI_EXIT_CODE, 10)
  : (meta.exitCode ?? 0);

const sleep = (ms) => (ms > 0 ? new Promise((resolve) => setTimeout(resolve, ms)) : Promise.resolve());

function write(stream, line) {
  return new Promise((resolve) => {
    const target = stream === 'stderr' ? process.stderr : process.stdout;
    if (!target.write(line + '\n')) target.once('drain', resolve);
    else resolve();
  });
}

async function readStdin() {
  if (process.env.FAKE_CLI_NO_STDIN === '1' || process.stdin.isTTY) return '';
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}

const stdin = await readStdin();

if (process.env.FAKE_CLI_CAPTURE) {
  const capture = {
    fixture: path.resolve(fixturePath),
    scenario: meta.scenario ?? null,
    cli: meta.cli ?? null,
    argv,
    cwd: process.cwd(),
    stdinChars: stdin.length,
    stdinSha256: crypto.createHash('sha256').update(stdin, 'utf8').digest('hex'),
    env: Object.fromEntries(
      Object.entries(process.env).filter(([key]) => /^(JOB_RESULTS_DIR|CLAUDE_|CODEX_|RUNNER_|FAKE_CLI_)/.test(key)),
    ),
  };
  fs.mkdirSync(path.dirname(path.resolve(process.env.FAKE_CLI_CAPTURE)), { recursive: true });
  fs.writeFileSync(process.env.FAKE_CLI_CAPTURE, JSON.stringify(capture, null, 2), 'utf8');
}

for (const step of steps) {
  await sleep(step.delay + perLineDelay);
  await write(step.stream, step.text);
}

process.exit(exitCode);
