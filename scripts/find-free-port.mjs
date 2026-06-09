#!/usr/bin/env node
// Robust free-port allocator for isolated worktree test stacks (ASS-1715).
//
// Why this exists: a task run inside a git worktree must bring up its OWN
// backend (+ frontend) for real tests without colliding with the long-lived
// stable stack (backend :5031 / frontend :4011) or a sibling worktree's stack.
// Fixed ports cannot satisfy that. This script asks the OS for free TCP ports
// the portable way - bind to port 0 and read back the kernel-assigned port -
// and reserves N of them at once so the caller gets N DISTINCT ports in a
// single shot (binding all sockets simultaneously before releasing any).
//
// There is an unavoidable TOCTOU window between releasing a reserved port and
// the consumer binding it. We keep it small (release immediately before
// printing) and the caller (worktree-test-stack.sh) retries on a failed boot.
// For parallel worktrees the practical collision probability is negligible
// because each worktree allocates independently from the ephemeral range.
//
// Usage:
//   node find-free-port.mjs                 # print one free port
//   node find-free-port.mjs --count 2       # print 2 distinct free ports (space-separated)
//   node find-free-port.mjs --count 2 --json
//   node find-free-port.mjs --host 0.0.0.0  # probe on a specific interface (default 127.0.0.1)
//   node find-free-port.mjs --self-test     # assert the allocator works; exit 0/1

import net from 'node:net';
import { pathToFileURL } from 'node:url';

function parseArgs(argv) {
  const opts = { count: 1, host: '127.0.0.1', json: false, selfTest: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--count') opts.count = Math.max(1, parseInt(argv[++i] ?? '1', 10) || 1);
    else if (a === '--host') opts.host = argv[++i] ?? '127.0.0.1';
    else if (a === '--json') opts.json = true;
    else if (a === '--self-test') opts.selfTest = true;
    else if (a === '-h' || a === '--help') opts.help = true;
    else throw new Error(`unknown argument: ${a}`);
  }
  return opts;
}

// Reserve a single free port by listening on 0. Resolves to { port, server }
// with the server still OPEN so the caller can hold several reservations at
// once (guaranteeing distinct ports) and close them together.
function reserveOne(host) {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    // exclusive:true so two concurrent allocations in the same process cannot
    // be handed the same port via address reuse.
    server.listen({ port: 0, host, exclusive: true }, () => {
      const addr = server.address();
      if (addr && typeof addr === 'object') resolve({ port: addr.port, server });
      else { server.close(); reject(new Error('could not read assigned port')); }
    });
  });
}

function closeAll(reservations) {
  return Promise.all(
    reservations.map((r) => new Promise((res) => r.server.close(() => res())))
  );
}

// Allocate `count` DISTINCT free ports. All reservation sockets are held open
// simultaneously so the kernel never hands the same port twice, then released.
export async function findFreePorts(count = 1, host = '127.0.0.1') {
  const reservations = [];
  try {
    for (let i = 0; i < count; i++) reservations.push(await reserveOne(host));
    return reservations.map((r) => r.port);
  } finally {
    await closeAll(reservations);
  }
}

async function selfTest() {
  const failures = [];
  // 1) returns the requested count of numeric ports
  const ports = await findFreePorts(3);
  if (ports.length !== 3) failures.push(`expected 3 ports, got ${ports.length}`);
  if (!ports.every((p) => Number.isInteger(p) && p > 0 && p < 65536)) {
    failures.push(`ports out of range: ${ports.join(',')}`);
  }
  // 2) ports are distinct
  if (new Set(ports).size !== ports.length) failures.push(`ports not distinct: ${ports.join(',')}`);
  // 3) each reported port is actually bindable right after allocation
  for (const p of ports) {
    const ok = await new Promise((resolve) => {
      const s = net.createServer();
      s.once('error', () => resolve(false));
      s.listen({ port: p, host: '127.0.0.1', exclusive: true }, () => s.close(() => resolve(true)));
    });
    if (!ok) failures.push(`port ${p} was not bindable after allocation`);
  }
  if (failures.length) {
    console.error('find-free-port self-test FAILED:');
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
  console.log(`find-free-port self-test OK (sample ports: ${ports.join(' ')})`);
  process.exit(0);
}

function printUsage() {
  console.log(
    [
      'find-free-port.mjs - allocate free TCP ports for isolated test stacks',
      '',
      'Usage:',
      '  node find-free-port.mjs [--count N] [--host H] [--json]',
      '  node find-free-port.mjs --self-test',
      '',
      'Prints N distinct free ports (space-separated, or JSON array with --json).',
    ].join('\n')
  );
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.help) return printUsage();
  if (opts.selfTest) return selfTest();
  const ports = await findFreePorts(opts.count, opts.host);
  if (opts.json) console.log(JSON.stringify(ports));
  else console.log(ports.join(' '));
}

// Only run the CLI when executed directly (not when imported by a test).
const invokedDirectly = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (invokedDirectly) {
  main().catch((err) => {
    console.error(`find-free-port: ${err.message}`);
    process.exit(1);
  });
}
