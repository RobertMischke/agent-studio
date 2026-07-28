#!/usr/bin/env node
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
const controlPort = positiveInt('REMOTE_HARNESS_CONTROL_PORT');
let claim = await loadState();
let renewals = 0;
let renewalFailures = 0;
let lastRenewalAt = null;
let lastFailure = null;
let heartbeatRunning = false;
let stopping = false;

await register();
if (claim) startHeartbeat();

const control = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url ?? '/', 'http://127.0.0.1');
    if (request.method === 'GET' && (url.pathname === '/healthz' || url.pathname === '/status')) {
      return json(response, 200, status());
    }
    if (request.method === 'POST' && url.pathname === '/claim') {
      if (claim) return json(response, 409, { error: 'claim-already-active', ...status() });
      const result = await api(`/api/v1/runners/${encodeURIComponent(runnerId)}/claims`, {
        method: 'POST',
        body: {
          runnerId,
          instanceId,
          requestedTtlSeconds: ttlSeconds,
          availableSlots: 1,
          inventory: inventory(null),
          requiredCapabilities: []
        }
      });
      if (result.status !== 200 || result.value?.status !== 'claimed') {
        return json(response, result.status === 200 ? 409 : result.status, result.value);
      }
      claim = result.value;
      await saveState(claim);
      startHeartbeat();
      return json(response, 200, claim);
    }
    if (request.method === 'POST' && url.pathname === '/release') {
      if (!claim) return json(response, 409, { error: 'no-active-claim' });
      const result = await api(`/api/v1/runs/${encodeURIComponent(claim.run.runId)}/lease/release`, {
        method: 'POST',
        body: authority({ outcome: 'remote-harness-release' })
      });
      if (result.status !== 200) return json(response, result.status, result.value);
      claim = null;
      await rm(stateFile, { force: true });
      return json(response, 200, result.value);
    }
    if (request.method === 'POST' && url.pathname === '/forget') {
      claim = null;
      await rm(stateFile, { force: true });
      return json(response, 200, status());
    }
    return json(response, 404, { error: 'not-found' });
  } catch (error) {
    return json(response, 500, { error: String(error?.message ?? error) });
  }
});
control.listen(controlPort, '0.0.0.0');

function startHeartbeat() {
  if (heartbeatRunning) return;
  heartbeatRunning = true;
  void heartbeatLoop();
}

async function heartbeatLoop() {
  while (!stopping && claim) {
    await delay(heartbeatMs);
    if (stopping || !claim) break;
    try {
      const result = await api(`/api/v1/runs/${encodeURIComponent(claim.run.runId)}/lease/renew`, {
        method: 'POST',
        body: authority({
          requestedTtlSeconds: ttlSeconds,
          inventory: inventory(claim)
        })
      });
      if (result.status !== 200) {
        renewalFailures++;
        lastFailure = { at: new Date().toISOString(), status: result.status, detail: result.value };
        continue;
      }
      renewals++;
      lastRenewalAt = new Date().toISOString();
      lastFailure = null;
      claim.lease = result.value.lease;
      await saveState(claim);
    } catch (error) {
      renewalFailures++;
      lastFailure = { at: new Date().toISOString(), status: 0, detail: String(error?.message ?? error) };
    }
  }
  heartbeatRunning = false;
}

async function register() {
  const result = await api(`/api/v1/runners/${encodeURIComponent(runnerId)}`, {
    method: 'PUT',
    body: {
      name: runnerId,
      hostId,
      instanceId,
      runnerVersion: 'remote-compose-harness/1',
      protocolVersion: 2,
      capabilities: ['coding-executor']
    }
  });
  if (result.status !== 200) throw new Error(`Runner registration failed (${result.status})`);
}

function authority(extra) {
  return {
    runnerId,
    instanceId,
    leaseId: claim.lease.leaseId,
    fence: claim.lease.fence,
    ...extra
  };
}

function inventory(activeClaim) {
  return {
    observedAt: new Date().toISOString(),
    processes: activeClaim ? [{
      runId: activeClaim.run.runId,
      taskKey: activeClaim.task.taskKey,
      pid: process.pid,
      cwd: '/var/lib/remote-harness-runner',
      startedAt: activeClaim.lease.acquiredAt
    }] : []
  };
}

function status() {
  return {
    status: 'ready',
    runnerId,
    instanceId,
    processId: process.pid,
    renewals,
    renewalFailures,
    lastRenewalAt,
    lastFailure,
    claim
  };
}

async function api(route, { method, body }) {
  const response = await fetch(`${serverUrl}${route}`, {
    method,
    headers: {
      Authorization: `Bearer ${authToken}`,
      'Content-Type': 'application/json',
      'X-Actor-Id': runnerId,
      'X-Client-Id': runnerId,
      'X-Task-Protocol-Version': '2',
      'X-Task-Client-Version': 'remote-compose-harness/1'
    },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  let value = null;
  try {
    value = text ? JSON.parse(text) : null;
  } catch {
    value = { raw: text };
  }
  return { status: response.status, value };
}

async function loadState() {
  try {
    return JSON.parse(await readFile(stateFile, 'utf8'));
  } catch (error) {
    if (error?.code === 'ENOENT') return null;
    throw error;
  }
}

async function saveState(value) {
  await mkdir(path.dirname(stateFile), { recursive: true });
  const temporary = `${stateFile}.${process.pid}.tmp`;
  await writeFile(temporary, `${JSON.stringify(value)}\n`, { mode: 0o600 });
  await rename(temporary, stateFile);
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
