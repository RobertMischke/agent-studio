#!/usr/bin/env node
// Hits each polled API endpoint N times, captures wall-clock timings
// (from outside the .NET process), computes p50/p95/p99/min/max/mean,
// writes JSON to logs/perf/live-<scenario>-latest.json so the HTML
// generator can include it next to the in-process backend baseline.
//
// Usage:
//   node tools/perf-report/measure-live-api.mjs [scenario] [iterations]
// Defaults: scenario="baseline", iterations=30.
// Requires the dev backend listening on http://localhost:5030.

import { writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { execSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, '..', '..');

const scenario = process.argv[2] || 'baseline';
const iterations = parseInt(process.argv[3] || '30', 10);
const warmup = 3;
const baseUrl = process.env.PERF_BASE_URL || 'http://localhost:5030';

// First fetch the watch-paths so we can pick a real project name and a real
// job id for the per-job endpoints. The numbers only mean something if the
// requests actually exercise the hot paths against the real workspace.
async function bootstrap() {
  const watch = await (await fetch(`${baseUrl}/api/watch-paths`)).json();
  if (!Array.isArray(watch) || watch.length === 0) {
    throw new Error('No watch paths configured on the dev backend');
  }
  const grouped = await (await fetch(`${baseUrl}/api/jobs/grouped`)).json();
  // Pick a job id from the largest non-empty lane (archive usually wins).
  const lanes = ['archive', 'completed', 'humanReview', 'autoReview', 'ready', 'progress', 'backlog', 'preparation'];
  let sampleJob = null;
  for (const lane of lanes) {
    const arr = grouped[lane];
    if (Array.isArray(arr) && arr.length > 0) {
      sampleJob = arr[Math.floor(arr.length / 2)];
      break;
    }
  }
  if (!sampleJob) throw new Error('No jobs found in any lane');
  return { watch, sampleJob, totalJobs: Object.values(grouped).flat().length };
}

async function timeOnce(url) {
  const t0 = performance.now();
  const r = await fetch(url, { headers: { 'cache-control': 'no-cache' } });
  // Read body so we measure real end-to-end (network + serialize), matching
  // what the browser polling pays.
  await r.arrayBuffer();
  return performance.now() - t0;
}

function quantile(sorted, q) {
  if (sorted.length === 1) return sorted[0];
  const pos = q * (sorted.length - 1);
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  if (lo === hi) return sorted[lo];
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}

function stats(samples) {
  const sorted = [...samples].sort((a, b) => a - b);
  const sum = sorted.reduce((a, b) => a + b, 0);
  return {
    iterations: samples.length,
    minMs: sorted[0],
    p50Ms: quantile(sorted, 0.5),
    p95Ms: quantile(sorted, 0.95),
    p99Ms: quantile(sorted, 0.99),
    maxMs: sorted[sorted.length - 1],
    meanMs: sum / sorted.length,
  };
}

async function measure(name, url, n = iterations) {
  for (let i = 0; i < warmup; i++) await timeOnce(url);
  const samples = [];
  for (let i = 0; i < n; i++) samples.push(await timeOnce(url));
  return { name, url, jobCount: 0, stats: stats(samples) };
}

function gitOut(args) {
  try { return execSync(`git ${args}`, { cwd: repoRoot }).toString().trim(); } catch { return 'unknown'; }
}

(async () => {
  const { watch, sampleJob, totalJobs } = await bootstrap();
  console.log(`Live perf measurement: scenario="${scenario}", iterations=${iterations}, totalJobs=${totalJobs}`);
  console.log(`Sample job: ${sampleJob.id} in project "${sampleJob.projectName}"`);

  const projects = watch.map(w => w.name);
  const sampleWatchPath = encodeURIComponent(sampleJob.watchPath);

  const endpoints = [
    { name: 'GET /api/watch-paths',                      url: `${baseUrl}/api/watch-paths` },
    { name: 'GET /api/jobs',                             url: `${baseUrl}/api/jobs` },
    { name: 'GET /api/jobs/grouped',                     url: `${baseUrl}/api/jobs/grouped` },
    { name: `GET /api/jobs/{id}`,                        url: `${baseUrl}/api/jobs/${encodeURIComponent(sampleJob.id)}?watchPath=${sampleWatchPath}` },
    { name: `GET /api/jobs/{id}/output`,                 url: `${baseUrl}/api/jobs/${encodeURIComponent(sampleJob.id)}/output?watchPath=${sampleWatchPath}` },
    { name: `GET /api/jobs/{id}/runs`,                   url: `${baseUrl}/api/jobs/${encodeURIComponent(sampleJob.id)}/runs?watchPath=${sampleWatchPath}` },
    { name: 'GET /api/runner/status',                    url: `${baseUrl}/api/runner/status` },
    { name: 'GET /api/cli/usage',                        url: `${baseUrl}/api/cli/usage` },
    { name: 'GET /api/cli/quota',                        url: `${baseUrl}/api/cli/quota` },
  ];
  for (const project of projects) {
    const enc = encodeURIComponent(project);
    endpoints.push({ name: `GET /api/runner/${project}/pending-decisions`, url: `${baseUrl}/api/runner/${enc}/pending-decisions` });
    endpoints.push({ name: `GET /api/runner/${project}/orchestrator-log`,  url: `${baseUrl}/api/runner/${enc}/orchestrator-log` });
  }

  const results = [];
  for (const ep of endpoints) {
    process.stdout.write(`  ${ep.name.padEnd(55)} `);
    try {
      const m = await measure(ep.name, ep.url);
      results.push(m);
      const s = m.stats;
      console.log(
        `p50=${s.p50Ms.toFixed(1).padStart(7)}ms  p95=${s.p95Ms.toFixed(1).padStart(7)}ms  p99=${s.p99Ms.toFixed(1).padStart(7)}ms  max=${s.maxMs.toFixed(1).padStart(7)}ms`
      );
    } catch (err) {
      console.log(`ERROR: ${err.message}`);
      results.push({ name: ep.name, url: ep.url, jobCount: 0, error: err.message });
    }
  }

  const report = {
    schemaVersion: 1,
    generatedAt: new Date().toISOString(),
    scenario,
    machineOs: process.platform,
    machineCpu: process.env.PROCESSOR_IDENTIFIER || 'unknown',
    logicalCores: (await import('node:os')).cpus().length,
    gitHead: gitOut('rev-parse HEAD'),
    gitBranch: gitOut('rev-parse --abbrev-ref HEAD'),
    boardSize: { totalJobs, projects },
    endpoints: results,
  };

  const perfDir = join(repoRoot, 'logs', 'perf');
  mkdirSync(perfDir, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  const path = join(perfDir, `live-${scenario}-${stamp}.json`);
  const latest = join(perfDir, `live-${scenario}-latest.json`);
  writeFileSync(path, JSON.stringify(report, null, 2));
  writeFileSync(latest, JSON.stringify(report, null, 2));
  console.log(`\nWrote ${path}`);
})().catch(err => { console.error(err); process.exit(1); });
