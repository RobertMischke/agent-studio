#!/usr/bin/env node

const [apiOrigin = 'http://127.0.0.1:5031', serverOrigin = 'http://127.0.0.1:5071'] = process.argv.slice(2);
const protocolHeaders = { 'X-Task-Protocol-Version': '1' };

async function json(origin, path, init = {}) {
  const response = await fetch(`${origin.replace(/\/$/, '')}${path}`, {
    ...init,
    headers: { ...protocolHeaders, ...(init.headers ?? {}) },
  });
  if (!response.ok) {
    throw new Error(`${origin}${path} returned HTTP ${response.status}: ${await response.text()}`);
  }
  return response.json();
}

const directStatus = await json(serverOrigin, '/api/v1/management/status');
const proxyStatus = await json(apiOrigin, '/api/v1/management/status');
if (!directStatus.authorityReady || !proxyStatus.authorityReady || directStatus.serverId !== proxyStatus.serverId) {
  throw new Error(
    `Proxy authority mismatch: direct=${directStatus.serverId}/${directStatus.authorityReady} `
    + `proxy=${proxyStatus.serverId}/${proxyStatus.authorityReady}`,
  );
}

const compatibility = await json(apiOrigin, '/api/v1/protocol/compatibility', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ clientKind: 'studio', clientVersion: 'stable-boot-probe', protocolVersion: 1 }),
});
if (!compatibility.supported) throw new Error('Task Server protocol compatibility was rejected.');

const projects = await json(apiOrigin, '/api/v1/projects');
await json(apiOrigin, '/api/v1/management/invariants');
const migration = await json(apiOrigin, '/api/v1/management/migrations/legacy/status');
if (!Array.isArray(projects)) throw new Error('Board projection did not return a project array.');
if (!migration.migrationId || migration.runs < migration.leases) {
  throw new Error('Stable proxy does not expose a complete legacy authority migration.');
}

console.log(
  `[stable-task-server-probe] Proxy and authority ready: server=${directStatus.serverId} `
  + `projects=${projects.length} runs=${migration.runs} reviews=${migration.reviews}`,
);
