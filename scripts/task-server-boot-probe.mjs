#!/usr/bin/env node

import process from 'node:process';

function argument(name, fallback) {
  const index = process.argv.indexOf(name);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const origin = argument('--url', 'http://127.0.0.1:5071').replace(/\/$/, '');
const timeoutMs = Number(argument('--timeout-ms', '60000'));
const deadline = Date.now() + timeoutMs;
let lastFailure = 'not attempted';

while (Date.now() < deadline) {
  try {
    const ready = await fetch(`${origin}/readyz`, { signal: AbortSignal.timeout(3000) });
    if (ready.ok) {
      const status = await fetch(`${origin}/api/v1/management/status`, {
        headers: { 'X-Task-Protocol-Version': '1' },
        signal: AbortSignal.timeout(3000),
      });
      if (status.ok) {
        const body = await status.json();
        if (body.authorityReady === true) {
          console.log(`Task Server ready: server=${body.serverId} mode=${body.mode} schema=${body.schemaVersion}`);
          process.exit(0);
        }
        lastFailure = `management status authorityReady=${body.authorityReady}`;
      } else {
        lastFailure = `management status HTTP ${status.status}`;
      }
    } else {
      lastFailure = `/readyz HTTP ${ready.status}`;
    }
  } catch (error) {
    lastFailure = error instanceof Error ? error.message : String(error);
  }
  await new Promise(resolve => setTimeout(resolve, 500));
}

console.error(`Task Server did not become authority-ready within ${timeoutMs}ms: ${lastFailure}`);
process.exit(1);
