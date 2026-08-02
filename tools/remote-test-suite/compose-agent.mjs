#!/usr/bin/env node
import { createHash } from 'node:crypto';
import http from 'node:http';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';

const serverUrl = required('REMOTE_HARNESS_SERVER_URL').replace(/\/$/, '');
const authToken = required('REMOTE_HARNESS_AUTH_TOKEN');
const runnerId = required('REMOTE_HARNESS_RUNNER_ID');
const instanceId = required('REMOTE_HARNESS_RUNNER_INSTANCE');
const hostId = required('REMOTE_HARNESS_RUNNER_HOST');
const stateFile = required('REMOTE_HARNESS_RUNNER_STATE');
const ttlSeconds = positiveInt('REMOTE_HARNESS_RUNNER_TTL_SECONDS');
const heartbeatMs = positiveInt('REMOTE_HARNESS_RUNNER_HEARTBEAT_MS');
const workIntervalMs = positiveInt('REMOTE_HARNESS_WORK_INTERVAL_MS');
const safetyMarginMs = positiveInt('REMOTE_HARNESS_AUTHORITY_SAFETY_MARGIN_MS');
const maxSlots = positiveInt('REMOTE_HARNESS_MAX_SLOTS');
const controlPort = positiveInt('REMOTE_HARNESS_CONTROL_PORT');
let state = await loadState();
let stopping = false;
let saveSequence = 0;
let saveChain = Promise.resolve();

await registerWithRetry();
void heartbeatLoop();
void workLoop();

const control = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url ?? '/', 'http://127.0.0.1');
    if (request.method === 'GET' && (url.pathname === '/healthz' || url.pathname === '/status')) {
      return json(response, 200, status());
    }
    if (request.method === 'POST' && url.pathname === '/claim') {
      const active = activeSlots();
      if (active.length >= maxSlots) {
        return json(response, 409, { error: 'all-slots-active', ...status() });
      }
      const result = await api(`/api/v1/runners/${encodeURIComponent(runnerId)}/claims`, {
        method: 'POST',
        body: {
          runnerId,
          instanceId,
          requestedTtlSeconds: ttlSeconds,
          availableSlots: maxSlots - active.length,
          inventory: inventory(),
          requiredCapabilities: []
        }
      });
      if (result.status !== 200 || result.value?.status !== 'claimed') {
        state.refusedClaims++;
        await saveState();
        return json(response, result.status === 200 ? 409 : result.status || 503, result.value);
      }
      const slot = createSlot(result.value);
      state.slots.push(slot);
      timeline('claim', slot, {
        leaseExpiresAt: slot.authority.leaseExpiresAt,
        stopBefore: slot.authority.stopBefore
      });
      await saveState();
      return json(response, 200, result.value);
    }
    if (request.method === 'POST' && url.pathname === '/finish-all') {
      const finishing = state.slots.filter(slot => slot.phase === 'working');
      for (const slot of finishing) queueTerminal(slot);
      await saveState();
      return json(response, 202, {
        queued: finishing.length,
        runs: finishing.map(slot => slot.claim.run.runId),
        status: status()
      });
    }
    if (request.method === 'POST' && url.pathname === '/release') {
      const slot = activeSlots()[0];
      if (!slot) return json(response, 409, { error: 'no-active-claim' });
      const result = await api(`/api/v1/runs/${encodeURIComponent(slot.claim.run.runId)}/lease/release`, {
        method: 'POST',
        body: authority(slot, { outcome: 'remote-harness-release' })
      });
      if (result.status !== 200) return json(response, result.status || 503, result.value);
      slot.phase = 'released';
      slot.generation.alive = false;
      slot.generation.deathProven = true;
      timeline('released', slot);
      await saveState();
      return json(response, 200, result.value);
    }
    if (request.method === 'POST' && url.pathname === '/forget') {
      state.slots = state.slots.filter(slot => !isActive(slot));
      await saveState();
      return json(response, 200, status());
    }
    return json(response, 404, { error: 'not-found' });
  } catch (error) {
    return json(response, 500, { error: String(error?.message ?? error) });
  }
});
control.listen(controlPort, '0.0.0.0');

async function heartbeatLoop() {
  while (!stopping) {
    await delay(heartbeatMs);
    for (const slot of activeSlots()) {
      if (stopping) break;
      await renewAndReplay(slot);
    }
  }
}

async function workLoop() {
  while (!stopping) {
    await delay(workIntervalMs);
    const now = Date.now();
    for (const slot of state.slots.filter(item => item.phase === 'working')) {
      if (slot.authority.uncertainSince && now >= Date.parse(slot.authority.stopBefore)) {
        slot.phase = 'authority-deadline-exhausted';
        slot.generation.alive = false;
        slot.generation.deathProven = true;
        slot.terminal = {
          outcome: 'LeaseLoss',
          reason: 'Local autonomy deadline exhausted; contained generation was reaped.'
        };
        timeline('authority-deadline-exhausted', slot, {
          stopBefore: slot.authority.stopBefore,
          generationDeathProven: true
        });
        continue;
      }
      slot.workUnits++;
      slot.lastUsefulWorkAt = new Date().toISOString();
      enqueue(slot, 'event', {
        eventKind: 'runner.useful-work',
        payload: {
          workUnit: slot.workUnits,
          generationId: slot.generation.id,
          detail: `deterministic infrastructure work unit ${slot.workUnits}`
        },
        occurredAt: slot.lastUsefulWorkAt
      });
      timeline('useful-work', slot, { workUnit: slot.workUnits });
    }
    await saveState();
  }
}

async function renewAndReplay(slot) {
  try {
    const result = await api(`/api/v1/runs/${encodeURIComponent(slot.claim.run.runId)}/lease/renew`, {
      method: 'POST',
      body: authority(slot, {
        requestedTtlSeconds: ttlSeconds,
        inventory: inventory()
      })
    });
    if (result.status !== 200 || !result.value?.lease) {
      if (result.status >= 400 && result.status < 500
          && result.status !== 408 && result.status !== 429) {
        slot.phase = 'fenced';
        slot.generation.alive = false;
        slot.generation.deathProven = true;
        slot.lastFailure = {
          at: new Date().toISOString(),
          status: result.status,
          detail: result.value
        };
        timeline('authority-rejected-generation-reaped', slot, {
          status: result.status,
          generationDeathProven: true
        });
        await saveState();
        return;
      }
      throw new Error(`lease renewal returned ${result.status}: ${JSON.stringify(result.value)}`);
    }

    const wasUncertain = slot.authority.uncertainSince;
    slot.renewals++;
    slot.lastRenewalAt = new Date().toISOString();
    slot.lastFailure = null;
    slot.claim.lease = result.value.lease;
    slot.authority.leaseExpiresAt = result.value.lease.expiresAt;
    slot.authority.stopBefore = stopBefore(result.value.lease.expiresAt);
    slot.authority.reconciledAt = slot.lastRenewalAt;
    slot.authority.uncertainSince = null;
    if (wasUncertain) {
      timeline('authority-reconciled-before-replay', slot, {
        uncertainSince: wasUncertain,
        reconciledAt: slot.authority.reconciledAt,
        fence: slot.claim.lease.fence
      });
    }
    await saveState();
    await replay(slot);
  } catch (error) {
    slot.renewalFailures++;
    slot.lastFailure = {
      at: new Date().toISOString(),
      status: 0,
      detail: String(error?.message ?? error)
    };
    if (!slot.authority.uncertainSince) {
      slot.authority.uncertainSince = slot.lastFailure.at;
      timeline('transport-uncertain', slot, {
        stopBefore: slot.authority.stopBefore
      });
    }
    if (Date.now() >= Date.parse(slot.authority.stopBefore)) {
      slot.phase = 'authority-deadline-exhausted';
      slot.generation.alive = false;
      slot.generation.deathProven = true;
      slot.terminal = {
        outcome: 'LeaseLoss',
        reason: 'Local autonomy deadline exhausted; contained generation was reaped.'
      };
      timeline('authority-deadline-exhausted', slot, {
        stopBefore: slot.authority.stopBefore,
        generationDeathProven: true
      });
    }
    await saveState();
  }
}

async function replay(slot) {
  if (slot.authority.uncertainSince) return;
  const pending = slot.outbox.filter(item => item.sequence > slot.lastAcknowledgedSequence);
  if (pending.length === 0) {
    await reportOutbox(slot);
    return;
  }
  const duplicateProof = slot.duplicateRecoveryReplay;
  for (const item of pending) {
    await sendItem(slot, item);
    if (duplicateProof) await sendItem(slot, item);
    slot.lastAcknowledgedSequence = item.sequence;
    await saveState();
  }
  slot.duplicateRecoveryReplay = false;
  if (slot.phase === 'terminal-queued') {
    slot.phase = 'completed';
    slot.generation.alive = false;
    slot.generation.deathProven = true;
    timeline('completion-acknowledged', slot, {
      resultSha: slot.terminal.resultSha,
      lastAcknowledgedSequence: slot.lastAcknowledgedSequence
    });
  }
  await reportOutbox(slot);
  await saveState();
}

async function sendItem(slot, item) {
  const runId = encodeURIComponent(slot.claim.run.runId);
  if (item.kind === 'event') {
    return requireAccepted(await api(`/api/v1/runs/${runId}/events`, {
      method: 'POST',
      body: {
        eventId: `evt_${shortHash(item.idempotencyKey)}`,
        kind: item.payload.eventKind,
        payloadJson: JSON.stringify(item.payload.payload),
        idempotencyKey: item.idempotencyKey,
        fence: slot.claim.lease.fence,
        occurredAt: item.payload.occurredAt,
        runnerId,
        instanceId,
        leaseId: slot.claim.lease.leaseId,
        sequence: item.sequence
      }
    }), item);
  }
  if (item.kind === 'artifact') {
    return requireAccepted(await api(`/api/v1/runs/${runId}/artifacts`, {
      method: 'POST',
      body: {
        artifactId: `art_${shortHash(item.idempotencyKey)}`,
        ...item.payload,
        idempotencyKey: item.idempotencyKey,
        fence: slot.claim.lease.fence,
        runnerId,
        instanceId,
        leaseId: slot.claim.lease.leaseId,
        sequence: item.sequence
      }
    }), item);
  }
  if (item.kind === 'result-handoff') {
    return requireAccepted(await api(`/api/v1/runs/${runId}/result-handoff`, {
      method: 'PUT',
      body: authority(slot, {
        sequence: item.sequence,
        idempotencyKey: item.idempotencyKey,
        envelopeDigest: item.payload.envelopeDigest,
        envelope: item.payload.envelope
      })
    }), item);
  }
  if (item.kind === 'completion') {
    return requireAccepted(await api(`/api/v1/runs/${runId}/completion`, {
      method: 'POST',
      body: authority(slot, {
        ...item.payload,
        idempotencyKey: item.idempotencyKey,
        sequence: item.sequence
      })
    }), item);
  }
  throw new Error(`Unknown outbox item kind '${item.kind}'.`);
}

function requireAccepted(result, item) {
  if (result.status < 200 || result.status >= 300) {
    throw new Error(
      `${item.kind} sequence ${item.sequence} returned ${result.status}: ${JSON.stringify(result.value)}`);
  }
  return result.value;
}

async function reportOutbox(slot) {
  const pending = slot.outbox.filter(item => item.sequence > slot.lastAcknowledgedSequence);
  const result = await api(`/api/v1/runners/${encodeURIComponent(runnerId)}/outbox-status`, {
    method: 'PUT',
    body: {
      instanceId,
      lastSequence: slot.outbox.at(-1)?.sequence ?? 0,
      lastAcknowledgedSequence: slot.lastAcknowledgedSequence,
      backlogCount: pending.length,
      oldestUnacknowledgedSequence: pending[0]?.sequence ?? null,
      finalHandoffState: slot.phase === 'completed' ? 'completed' : slot.phase,
      runId: slot.claim.run.runId,
      envelopeDigest: slot.terminal?.envelopeDigest ?? null,
      observedAt: new Date().toISOString()
    }
  });
  if (result.status < 200 || result.status >= 300) {
    throw new Error(`outbox report returned ${result.status}: ${JSON.stringify(result.value)}`);
  }
}

function queueTerminal(slot) {
  const completedAt = new Date().toISOString();
  enqueue(slot, 'event', {
    eventKind: 'runner.terminal',
    payload: {
      outcome: 'Done',
      detail: 'Deterministic useful work completed during the Task Server outage.'
    },
    occurredAt: completedAt
  });

  const artifactBytes = Buffer.from(JSON.stringify({
    runId: slot.claim.run.runId,
    workUnits: slot.workUnits,
    firstUsefulWorkAt: slot.firstUsefulWorkAt,
    lastUsefulWorkAt: slot.lastUsefulWorkAt,
    completedAt
  }));
  const artifactSha = sha256(artifactBytes);
  enqueue(slot, 'artifact', {
    name: `results/autonomy-${slot.claim.task.taskKey}.json`,
    mediaType: 'application/json',
    contentBase64: artifactBytes.toString('base64'),
    sha256: artifactSha
  });

  const baseSha = sha256(`base:${slot.claim.run.runId}`).slice(0, 40);
  const resultSha = sha256(`result:${slot.claim.run.runId}:${slot.workUnits}`).slice(0, 40);
  const bundleDigest = sha256(`bundle:${slot.claim.run.runId}:${artifactSha}`);
  const manifestDigest = sha256(JSON.stringify([{
    name: `results/autonomy-${slot.claim.task.taskKey}.json`,
    sha256: artifactSha
  }]));
  const envelope = {
    repositoryId: slot.claim.task.projectId,
    sourceRunAttemptId: slot.claim.run.runId,
    baseSha,
    resultSha,
    immutableRemoteRef: null,
    sourceBundleDigest: bundleDigest,
    artifactManifestDigest: manifestDigest,
    submodules: [],
    lfsObjects: [],
    repositoryUrl: null
  };
  const envelopeDigest = sha256(JSON.stringify(envelope));
  enqueue(slot, 'result-handoff', { envelopeDigest, envelope });
  enqueue(slot, 'completion', {
    outcome: 'Done',
    summary: `Autonomy canary completed ${slot.workUnits} useful work units.`,
    resultEnvelopeDigest: envelopeDigest
  });
  slot.phase = 'terminal-queued';
  slot.terminal = { resultSha, envelopeDigest, completedAt };
  slot.duplicateRecoveryReplay = true;
  timeline('terminal-reports-journaled', slot, {
    resultSha,
    outboxBacklog: backlog(slot)
  });
}

function enqueue(slot, kind, payload) {
  const sequence = (slot.outbox.at(-1)?.sequence ?? 0) + 1;
  slot.outbox.push({
    sequence,
    kind,
    payload,
    idempotencyKey: `${slot.claim.run.runId}:${sequence}`,
    createdAt: new Date().toISOString()
  });
}

function createSlot(claim) {
  const acquiredAt = new Date().toISOString();
  return {
    claim,
    phase: 'working',
    renewals: 0,
    renewalFailures: 0,
    lastRenewalAt: null,
    lastFailure: null,
    workUnits: 0,
    firstUsefulWorkAt: acquiredAt,
    lastUsefulWorkAt: null,
    outbox: [],
    lastAcknowledgedSequence: 0,
    duplicateRecoveryReplay: false,
    terminal: null,
    generation: {
      id: `${claim.run.runId}:${claim.lease.fence}`,
      alive: true,
      deathProven: false
    },
    authority: {
      leaseExpiresAt: claim.lease.expiresAt,
      stopBefore: stopBefore(claim.lease.expiresAt),
      uncertainSince: null,
      reconciledAt: acquiredAt
    }
  };
}

function stopBefore(expiresAt) {
  return new Date(Date.parse(expiresAt) - safetyMarginMs).toISOString();
}

function authority(slot, extra) {
  return {
    runnerId,
    instanceId,
    leaseId: slot.claim.lease.leaseId,
    fence: slot.claim.lease.fence,
    ...extra
  };
}

function inventory() {
  return {
    observedAt: new Date().toISOString(),
    processes: activeSlots().map((slot, index) => ({
      runId: slot.claim.run.runId,
      taskKey: slot.claim.task.taskKey,
      pid: process.pid + index,
      cwd: `/var/lib/remote-harness-runner/${slot.claim.run.runId}`,
      startedAt: slot.claim.lease.acquiredAt
    }))
  };
}

function activeSlots() {
  return state.slots.filter(isActive);
}

function isActive(slot) {
  return ['working', 'terminal-queued', 'authority-deadline-exhausted', 'fenced']
    .includes(slot.phase);
}

function backlog(slot) {
  return slot.outbox.filter(item => item.sequence > slot.lastAcknowledgedSequence).length;
}

function timeline(kind, slot, detail = {}) {
  state.timeline.push({
    at: new Date().toISOString(),
    kind,
    runId: slot?.claim?.run?.runId ?? null,
    taskKey: slot?.claim?.task?.taskKey ?? null,
    fence: slot?.claim?.lease?.fence ?? null,
    ...detail
  });
  if (state.timeline.length > 4000) state.timeline.splice(0, state.timeline.length - 4000);
}

function status() {
  const active = activeSlots();
  return {
    status: 'ready',
    runnerId,
    instanceId,
    processId: process.pid,
    renewals: state.slots.reduce((total, slot) => total + slot.renewals, 0),
    renewalFailures: state.slots.reduce((total, slot) => total + slot.renewalFailures, 0),
    lastRenewalAt: state.slots.map(slot => slot.lastRenewalAt).filter(Boolean).sort().at(-1) ?? null,
    lastFailure: state.slots.map(slot => slot.lastFailure).filter(Boolean).sort((a, b) => a.at.localeCompare(b.at)).at(-1) ?? null,
    claim: active[0]?.claim ?? null,
    claims: active.map(slot => slot.claim),
    refusedClaims: state.refusedClaims,
    slots: state.slots.map(slot => ({
      runId: slot.claim.run.runId,
      taskKey: slot.claim.task.taskKey,
      fence: slot.claim.lease.fence,
      phase: slot.phase,
      renewals: slot.renewals,
      renewalFailures: slot.renewalFailures,
      workUnits: slot.workUnits,
      firstUsefulWorkAt: slot.firstUsefulWorkAt,
      lastUsefulWorkAt: slot.lastUsefulWorkAt,
      backlogCount: backlog(slot),
      lastSequence: slot.outbox.at(-1)?.sequence ?? 0,
      lastAcknowledgedSequence: slot.lastAcknowledgedSequence,
      resultSha: slot.terminal?.resultSha ?? null,
      generation: slot.generation,
      authority: slot.authority
    })),
    timeline: state.timeline
  };
}

async function registerWithRetry() {
  let attempt = 0;
  while (!stopping) {
    const result = await api(`/api/v1/runners/${encodeURIComponent(runnerId)}`, {
      method: 'PUT',
      body: {
        name: runnerId,
        hostId,
        instanceId,
        runnerVersion: 'remote-compose-harness/2',
        protocolVersion: 2,
        capabilities: [
          'coding-executor',
          'durable-result-handoff',
          'host-outbox-replay'
        ]
      }
    });
    if (result.status === 200) return;
    attempt++;
    await delay(Math.min(10_000, attempt * 1000));
  }
}

async function api(route, { method, body }) {
  try {
    const response = await fetch(`${serverUrl}${route}`, {
      method,
      headers: {
        Authorization: `Bearer ${authToken}`,
        'Content-Type': 'application/json',
        'X-Actor-Id': runnerId,
        'X-Client-Id': runnerId,
        'X-Task-Protocol-Version': '2',
        'X-Task-Client-Version': 'remote-compose-harness/2'
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(5000)
    });
    const text = await response.text();
    let value = null;
    try {
      value = text ? JSON.parse(text) : null;
    } catch {
      value = { raw: text };
    }
    return { status: response.status, value };
  } catch (error) {
    return { status: 0, value: { error: String(error?.message ?? error) } };
  }
}

async function loadState() {
  try {
    const value = JSON.parse(await readFile(stateFile, 'utf8'));
    if (value.schemaVersion === 2 && Array.isArray(value.slots)) return value;
    if (value.run && value.lease) {
      return {
        schemaVersion: 2,
        slots: [createSlot(value)],
        timeline: [],
        refusedClaims: 0
      };
    }
    throw new Error('Runner state schema is unsupported.');
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return {
        schemaVersion: 2,
        slots: [],
        timeline: [],
        refusedClaims: 0
      };
    }
    throw error;
  }
}

async function saveState() {
  const value = JSON.stringify(state);
  const writeId = ++saveSequence;
  saveChain = saveChain.then(async () => {
    await mkdir(path.dirname(stateFile), { recursive: true });
    const temporary = `${stateFile}.${process.pid}.${writeId}.tmp`;
    await writeFile(temporary, `${value}\n`, { mode: 0o600 });
    await rename(temporary, stateFile);
  });
  await saveChain;
}

function shortHash(value) {
  return sha256(value).slice(0, 32);
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function required(name) {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`${name} is required`);
  return value;
}

function positiveInt(name) {
  const value = Number(required(name));
  if (!Number.isInteger(value) || value <= 0) throw new Error(`${name} must be a positive integer`);
  return value;
}

function json(response, statusCode, value) {
  const body = JSON.stringify(value);
  response.writeHead(statusCode, {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(body)
  });
  response.end(body);
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function shutdown() {
  stopping = true;
  control.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 5000).unref();
}
process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
