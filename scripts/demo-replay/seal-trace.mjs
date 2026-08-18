#!/usr/bin/env node
// Seals a public-demo replay trace (AGT-W34 slice S3).
//
// The signing key belongs to the release bundle build, never to the replay
// service: the demo runtime ships only pre-signed material, so a compromised
// replay process can re-emit the recorded scene but cannot mint a new frame.
//
//   node scripts/demo-replay/seal-trace.mjs keygen --out-dir <dir>
//   node scripts/demo-replay/seal-trace.mjs seal --trace <in.json> \
//        --private-key <key.pem> --key-id <id> --out <signed.json>
//
// The canonical bytes must match AgentStudio.TaskServer.Contracts exactly:
// camelCase property names in declaration order, no indentation, nulls kept.
import { createHash, createPrivateKey, generateKeyPairSync, sign } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const SCHEMA_VERSION = 1;
const ALGORITHM = 'ecdsa-p256-sha256';
const FRAME_KINDS = ['session.started', 'session.completed', 'turn.started', 'turn.completed', 'diagnostic'];

// .NET's default JSON encoder escapes anything outside this conservative set.
// Keeping authored text inside it is what makes both implementations agree
// byte for byte, so an unexpected character is a hard error, not a warning.
const SAFE_TEXT = /^[A-Za-z0-9 .,:;!?()[\]{}/*=_@#$%^~|\\-]*$/;

function fail(message) {
  console.error(`seal-trace: ${message}`);
  process.exit(1);
}

function flag(name, fallback = null) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && index + 1 < process.argv.length ? process.argv[index + 1] : fallback;
}

function safeText(value, where) {
  if (value === null || value === undefined) return null;
  const text = String(value).trim();
  if (text.length === 0) return null;
  if (!SAFE_TEXT.test(text)) fail(`${where} contains a character that the two JSON encoders escape differently: ${text}`);
  return text;
}

function canonicalFrame(frame, index) {
  const where = `frame ${index + 1}`;
  if (!Number.isInteger(frame.sequence) || frame.sequence <= 0) fail(`${where} needs a positive integer sequence`);
  if (!Number.isInteger(frame.offsetSeconds) || frame.offsetSeconds < 0) fail(`${where} needs a non-negative integer offsetSeconds`);
  if (!FRAME_KINDS.includes(frame.kind)) fail(`${where} kind must be one of ${FRAME_KINDS.join(', ')}`);
  // Declaration order of DemoReplayFrame. Do not reorder.
  return {
    sequence: frame.sequence,
    offsetSeconds: frame.offsetSeconds,
    taskKey: safeText(frame.taskKey, `${where} taskKey`),
    kind: frame.kind.trim(),
    message: safeText(frame.message, `${where} message`),
    sessionId: safeText(frame.sessionId, `${where} sessionId`),
    turnId: safeText(frame.turnId, `${where} turnId`),
    runIndex: frame.runIndex ?? null,
    cli: safeText(frame.cli, `${where} cli`),
    model: safeText(frame.model, `${where} model`),
    thinkingLevel: safeText(frame.thinkingLevel, `${where} thinkingLevel`),
    durationMs: frame.durationMs ?? null,
    inputTokens: frame.inputTokens ?? null,
    outputTokens: frame.outputTokens ?? null,
    reasoningTokens: frame.reasoningTokens ?? null,
  };
}

function validate(trace) {
  if (trace.schemaVersion !== SCHEMA_VERSION) fail(`schemaVersion must be ${SCHEMA_VERSION}`);
  if (!trace.traceId?.trim()) fail('traceId is required');
  if (!trace.sceneKey?.trim()) fail('sceneKey is required');
  if (!Array.isArray(trace.taskKeys) || trace.taskKeys.length === 0) fail('taskKeys must be a non-empty array');
  if (!Array.isArray(trace.frames) || trace.frames.length === 0) fail('frames must be a non-empty array');

  const declared = new Set(trace.taskKeys.map(key => String(key).trim()));
  if (declared.size !== trace.taskKeys.length) fail('taskKeys must be unique');

  let previousSequence = 0;
  let previousOffset = -1;
  for (const [index, frame] of trace.frames.entries()) {
    if (frame.sequence <= previousSequence) fail(`frame ${index + 1} sequence must increase strictly`);
    if (frame.offsetSeconds < previousOffset) fail(`frame ${index + 1} offsetSeconds must not decrease`);
    if (!declared.has(String(frame.taskKey).trim())) fail(`frame ${index + 1} targets an undeclared task key`);
    previousSequence = frame.sequence;
    previousOffset = frame.offsetSeconds;
  }
}

function canonicalTrace(trace) {
  return {
    schemaVersion: trace.schemaVersion,
    traceId: safeText(trace.traceId, 'traceId'),
    sceneKey: safeText(trace.sceneKey, 'sceneKey'),
    taskKeys: trace.taskKeys.map(key => String(key).trim()).sort(),
    frames: [...trace.frames].sort((a, b) => a.sequence - b.sequence).map(canonicalFrame),
  };
}

function digestOf(trace) {
  return createHash('sha256').update(Buffer.from(JSON.stringify(canonicalTrace(trace)), 'utf8')).digest('hex');
}

function framePayload(traceId, digest, frame, index) {
  // Mirrors DemoReplayTraceSignature.SealedFrame(TraceId, Digest, Frame).
  return Buffer.from(JSON.stringify({
    traceId: traceId.trim(),
    digest: digest.trim().toLowerCase(),
    frame: canonicalFrame(frame, index),
  }), 'utf8');
}

function signBytes(privateKey, payload) {
  return sign('sha256', payload, { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64');
}

function keygen() {
  const outDir = flag('out-dir') ?? fail('keygen requires --out-dir');
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  mkdirSync(outDir, { recursive: true });
  const privatePath = join(outDir, 'demo-replay-signing.pem');
  const publicPath = join(outDir, 'demo-replay-public.b64');
  writeFileSync(privatePath, privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
  writeFileSync(publicPath, `${publicKey.export({ type: 'spki', format: 'der' }).toString('base64')}\n`);
  console.log(`private key  ${privatePath}`);
  console.log(`public key   ${publicPath}`);
  console.log('Keep the private key in the release bundle build. It must never reach the demo VM.');
}

function seal() {
  const tracePath = flag('trace') ?? fail('seal requires --trace');
  const keyPath = flag('private-key') ?? fail('seal requires --private-key');
  const keyId = flag('key-id') ?? fail('seal requires --key-id');
  const outPath = flag('out') ?? fail('seal requires --out');

  const trace = JSON.parse(readFileSync(tracePath, 'utf8'));
  validate(trace);
  const canonical = canonicalTrace(trace);
  const digest = digestOf(trace);
  const privateKey = createPrivateKey(readFileSync(keyPath, 'utf8'));

  const signed = {
    trace: canonical,
    digest,
    algorithm: ALGORITHM,
    keyId: keyId.trim(),
    traceSignature: signBytes(privateKey, Buffer.from(digest, 'hex')),
    seals: canonical.frames.map((frame, index) => ({
      sequence: frame.sequence,
      signature: signBytes(privateKey, framePayload(canonical.traceId, digest, frame, index)),
    })),
  };

  writeFileSync(outPath, `${JSON.stringify(signed, null, 2)}\n`);
  console.log(`sealed       ${outPath}`);
  console.log(`traceId      ${canonical.traceId}`);
  console.log(`digest       ${digest}`);
  console.log(`frames       ${canonical.frames.length}`);
  console.log('Pin the digest as DemoReplay:TraceDigest on the demo server.');
}

const command = process.argv[2];
if (command === 'keygen') keygen();
else if (command === 'seal') seal();
else fail('usage: seal-trace.mjs <keygen|seal> [options]');
