#!/usr/bin/env node
/**
 * Generates and signs the fixed replay trace for the public demo
 * (AGT-W34 slice S3).
 *
 * The trace is the only content the replay plane can ever produce: the Task
 * Server holds a byte-identical copy and materializes each event from its own
 * side, so this file is what a reviewer reads to know exactly what a visitor
 * will see. It is deterministic by construction - no clock reads, no randomness
 * - so regenerating it must produce a byte-identical file.
 *
 * Scene: the two ADR-0056 demo tasks that sit in the 3-progress lane
 * (DEMO-4 large uploads, DEMO-12 export progress stream), matching the demo
 * content specification in docs/operations/demo-instanz/index.html.
 *
 * The canonical form implemented here must stay identical to
 * contracts/TaskServer.Contracts/DemoReplayContracts.cs
 * (DemoReplayTraceCanonicalizer). backend.Tests/DemoReplayTraceFixtureTests.cs
 * recomputes the committed digest in C# and fails when the two drift.
 *
 * Usage: node scripts/demo-replay/generate-trace.mjs [--output <path>] [--key <secret>]
 */

import { createHash, createHmac } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const DEFAULT_OUTPUT = resolve(HERE, '../../testdata/demo-replay/demo-replay-trace.json');

/**
 * Development signing key. It is not a secret: a public demo trace is public by
 * definition, and the digest is what the release manifest pins. A production
 * bundle re-signs the same canonical form with the operator key, which lives in
 * the bundle build and never on the replay host.
 */
const DEFAULT_KEY = 'demo-replay-development-key';
const KEY_ID = 'demo-replay-dev';

const CANONICAL_HEADER = 'agent-studio/demo-replay-trace/v1';
const SCHEMA_VERSION = 1;
const TRACE_ID = 'demo-instanz-cycle-1';
const CYCLE_SECONDS = 720;

const SCENE = {
  projects: ['Demo App'],
  taskKeys: ['DEMO-4', 'DEMO-12'],
};

/**
 * One pass of the scene. Offsets are seconds inside the 12-minute cycle; the
 * two tasks interleave so the board shows two cards moving rather than one.
 */
const SCRIPT = [
  [0, 'DEMO-4', 'session.started', 'Simulated run started for large avatar uploads.'],
  [12, 'DEMO-4', 'turn.started', 'Reading the upload size limits and the failing case.'],
  [48, 'DEMO-12', 'session.started', 'Simulated run started for the export progress stream.'],
  [66, 'DEMO-12', 'turn.started', 'Mapping the activity feed events the export should emit.'],
  [95, 'DEMO-4', 'turn.completed', 'Reproduced the failure for uploads above the configured limit.'],
  [130, 'DEMO-4', 'turn.started', 'Adding a bounded read and a typed rejection for oversized files.'],
  [168, 'DEMO-12', 'turn.completed', 'Listed the three progress checkpoints the feed is missing.'],
  [204, 'DEMO-12', 'turn.started', 'Emitting progress checkpoints from the export pipeline.'],
  [246, 'DEMO-4', 'turn.completed', 'Oversized uploads now fail with a typed message instead of a timeout.'],
  [282, 'DEMO-4', 'turn.started', 'Covering the boundary with a direct policy test.'],
  [318, 'DEMO-12', 'turn.completed', 'Progress checkpoints reach the activity feed in order.'],
  [354, 'DEMO-12', 'turn.started', 'Adding a regression test for the reconnect case.'],
  [402, 'DEMO-4', 'turn.completed', 'Policy test covers the limit, the boundary, and the rejection message.'],
  [444, 'DEMO-12', 'turn.completed', 'Reconnect replays the checkpoints the client missed.'],
  [486, 'DEMO-4', 'turn.started', 'Running the affected test project.'],
  [528, 'DEMO-12', 'turn.started', 'Running the export pipeline tests.'],
  [576, 'DEMO-4', 'turn.completed', 'Affected tests pass.'],
  [612, 'DEMO-12', 'turn.completed', 'Export pipeline tests pass.'],
  [648, 'DEMO-4', 'session.completed', 'Simulated run finished. Upload limit handling is covered.'],
  [684, 'DEMO-12', 'session.completed', 'Simulated run finished. Progress streaming is covered.'],
];

/** Deterministic token and duration figures derived from the step index only. */
function metrics(index, kind) {
  if (kind === 'session.started') return { durationMs: null, inputTokens: null, outputTokens: null };
  if (kind === 'session.completed') {
    return { durationMs: (index + 1) * 4000, inputTokens: 1800 + index * 40, outputTokens: 620 + index * 15 };
  }
  if (kind === 'turn.started') return { durationMs: null, inputTokens: null, outputTokens: null };
  return { durationMs: 18_000 + index * 900, inputTokens: 900 + index * 55, outputTokens: 240 + index * 20 };
}

function buildEvents() {
  return SCRIPT.map(([offsetSeconds, taskKey, kind, message], index) => ({
    sequence: index + 1,
    offsetMs: offsetSeconds * 1000,
    taskKey,
    kind,
    severity: 'info',
    message,
    ...metrics(index, kind),
  }));
}

function sha256Hex(value) {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

function number(value) {
  return value === null || value === undefined ? '' : String(value);
}

/** Must match DemoReplayTraceCanonicalizer.Canonicalize exactly. */
export function canonicalize(trace) {
  const lines = [
    CANONICAL_HEADER,
    `schemaVersion=${trace.schemaVersion}`,
    `traceId=${trace.traceId}`,
    `cycleSeconds=${trace.cycleSeconds}`,
  ];
  for (const project of trace.scene.projects) lines.push(`scene.project=${project}`);
  for (const taskKey of trace.scene.taskKeys) lines.push(`scene.taskKey=${taskKey}`);
  for (const step of trace.events) {
    lines.push([
      `event=${step.sequence}`,
      step.offsetMs,
      step.taskKey,
      step.kind,
      step.severity ?? '',
      number(step.durationMs),
      number(step.inputTokens),
      number(step.outputTokens),
      sha256Hex(step.message ?? ''),
    ].join('|'));
  }
  return `${lines.join('\n')}\n`;
}

export function buildTrace(signingKey) {
  const unsigned = {
    schemaVersion: SCHEMA_VERSION,
    traceId: TRACE_ID,
    cycleSeconds: CYCLE_SECONDS,
    scene: SCENE,
    events: buildEvents(),
  };
  const canonical = canonicalize(unsigned);
  return {
    ...unsigned,
    signature: {
      algorithm: 'hmac-sha256',
      keyId: KEY_ID,
      digest: sha256Hex(canonical),
      value: createHmac('sha256', signingKey).update(canonical, 'utf8').digest('hex'),
    },
  };
}

function parseArgs(argv) {
  const args = { output: DEFAULT_OUTPUT, key: DEFAULT_KEY };
  for (let index = 0; index < argv.length; index++) {
    if (argv[index] === '--output') args.output = resolve(argv[++index]);
    else if (argv[index] === '--key') args.key = argv[++index];
    else throw new Error(`Unknown argument: ${argv[index]}`);
  }
  return args;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const trace = buildTrace(args.key);
  writeFileSync(args.output, `${JSON.stringify(trace, null, 2)}\n`, 'utf8');
  console.log(`Wrote ${trace.events.length} steps over ${trace.cycleSeconds}s to ${args.output}`);
  console.log(`Trace digest: ${trace.signature.digest}`);
  console.log('Pin this digest in the release manifest and in DemoReplay:TraceDigest.');
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  main();
}
