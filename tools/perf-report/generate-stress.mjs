#!/usr/bin/env node
// Cycle 7g — render the dark-themed HTML report for the stress
// measurement (logs/perf/stress-<scenario>-<tag>.jsonl rows). Reads one
// JSONL per scenario, aligns by (N, metric), emits a side-by-side
// table per metric so cliffs are visible. Run as:
//   node tools/perf-report/generate-stress.mjs --scenarios stress-baseline --scenarios stress-after-7b --scenarios stress-final --out logs/perf/perf-stress-2026-05-09.html

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const perfDir = path.join(repoRoot, 'logs', 'perf');

const args = process.argv.slice(2);
const scenarios = [];
let outPath = null;
let title = 'Agent Software Studio - Render-Perf Stress Report';
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === '--scenarios') scenarios.push(args[++i]);
  else if (a === '--out') outPath = args[++i];
  else if (a === '--title') title = args[++i];
}
if (scenarios.length === 0) { console.error('Need at least one --scenarios'); process.exit(1); }

function readScenarioLatest(scn) {
  if (!fs.existsSync(perfDir)) return [];
  const files = fs.readdirSync(perfDir)
    .filter(f => f.startsWith(`stress-${scn}-`) && f.endsWith('.jsonl'));
  if (files.length === 0) return [];
  files.sort((a, b) => fs.statSync(path.join(perfDir, b)).mtimeMs - fs.statSync(path.join(perfDir, a)).mtimeMs);
  return fs.readFileSync(path.join(perfDir, files[0]), 'utf8')
    .split('\n').filter(Boolean).map(l => JSON.parse(l));
}

const data = scenarios.map(scn => ({ scn, rows: readScenarioLatest(scn) }));

// Build pivot: metric -> N -> scn -> { value, unit, notes }
const metrics = new Map();
for (const { scn, rows } of data) {
  for (const r of rows) {
    if (!metrics.has(r.metric)) metrics.set(r.metric, { unit: r.unit, byN: new Map() });
    const slot = metrics.get(r.metric);
    if (!slot.byN.has(r.N)) slot.byN.set(r.N, new Map());
    slot.byN.get(r.N).set(scn, { value: r.value, notes: r.notes });
  }
}

function fmt(value, unit) {
  if (value === null || value === undefined) return '-';
  if (unit === 'ms') return value < 10 ? value.toFixed(1) + 'ms' : Math.round(value) + 'ms';
  if (unit === 'bytes') {
    if (value >= 1024*1024) return (value/1024/1024).toFixed(1) + 'MB';
    if (value >= 1024) return (value/1024).toFixed(1) + 'KB';
    return Math.round(value) + 'B';
  }
  if (unit === 'fps') return value.toFixed(1) + ' fps';
  return Math.round(value).toString();
}
function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
}
function deltaCell(first, last, unit, metric) {
  if (typeof first !== 'number' || typeof last !== 'number' || first === 0) return `<td class="num delta-flat">-</td>`;
  const change = ((last - first) / first) * 100;
  // Lower-is-better for everything except fps + rendered-card-count
  // (where fewer cards is fine - that's virtualization working - so it
  // gets neutral coloring instead of red).
  const higherIsBetter = unit === 'fps';
  const neutralCount   = metric === 'rendered-card-count' || metric === 'cdp-document-count';
  if (neutralCount) {
    return `<td class="num delta-flat">${change >= 0 ? '+' : ''}${change.toFixed(0)}%</td>`;
  }
  const better = higherIsBetter ? change > 5  : change < -5;
  const worse  = higherIsBetter ? change < -5 : change > 5;
  const cls = better ? 'delta-better' : worse ? 'delta-worse' : 'delta-flat';
  const sign = change >= 0 ? '+' : '';
  return `<td class="num ${cls}">${sign}${change.toFixed(0)}%</td>`;
}

const css = `
  :root { color-scheme: dark; --bg:#111318; --panel:#181c24; --panel-2:#202633; --text:#eceff4;
          --muted:#aab2c0; --line:#344052; --ok:#73c991; --warn:#e6c07b; --bad:#ef767a; --info:#7fb4ff; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.55 "Segoe UI",system-ui,sans-serif; }
  main { max-width:1280px; margin:0 auto; padding:36px 24px 64px; }
  h1, h2, h3 { line-height:1.2; margin:0; }
  h1 { font-size:30px; }
  h2 { font-size:21px; margin-top:34px; border-top:1px solid var(--line); padding-top:24px; }
  h3 { font-size:16px; margin-top:22px; color:var(--muted); }
  p { margin:10px 0; } code { background:#0d1016; border:1px solid var(--line); border-radius:4px; padding:1px 5px; }
  .lead { color:var(--muted); font-size:16px; max-width:920px; }
  table { width:100%; border-collapse:collapse; margin-top:12px; background:var(--panel);
          border:1px solid var(--line); border-radius:8px; overflow:hidden; }
  th, td { text-align:left; padding:10px 12px; border-bottom:1px solid var(--line); vertical-align:top; }
  th { background:var(--panel-2); color:var(--muted); font-weight:600; }
  tr:last-child td { border-bottom:0; }
  .num { text-align:right; font-variant-numeric:tabular-nums; }
  .small { color:var(--muted); font-size:13px; }
  .tag { display:inline-block; border:1px solid var(--line); border-radius:999px; padding:2px 9px;
         margin-right:6px; color:var(--muted); font-size:12px; }
  .delta-better { color:var(--ok); font-weight:600; }
  .delta-worse  { color:var(--bad); font-weight:600; }
  .delta-flat   { color:var(--muted); }
  .verdict { background:linear-gradient(90deg,rgba(115,201,145,.16),rgba(127,180,255,.10));
             border:1px solid rgba(115,201,145,.45); border-radius:8px; padding:18px; margin-top:22px; }
`;

let html = `<!doctype html><html lang="en"><head><meta charset="utf-8">
<title>${escapeHtml(title)}</title><style>${css}</style></head><body><main>
<h1>${escapeHtml(title)}</h1>
<p class="lead">Generated ${new Date().toISOString().replace('T',' ').slice(0,19)} UTC. Runtime-axis measurement (per ADR-0033): each scenario hits N=10/100/200/500 synthetic kanban cards via Playwright + page.route fixture, captures render, DOM, longtask, scroll-FPS. Cliff = where a metric stops scaling.</p>
<div class="verdict"><strong>Scenarios:</strong> ${data.map(d => `<span class="tag">${escapeHtml(d.scn)}</span>`).join('')}<br>
<span class="small">Δ% column compares last scenario vs first. Negative is better for ms / bytes / count, positive is better for fps.</span></div>`;

const orderedMetrics = [
  'initial-render-to-first-card',
  'rendered-card-count',
  'dom-node-count',
  'cdp-node-count',
  'js-heap-bytes',
  'long-tasks-total-during-5s-idle',
  'scroll-fps-2s',
];
const allMetricKeys = orderedMetrics.filter(m => metrics.has(m))
  .concat([...metrics.keys()].filter(m => !orderedMetrics.includes(m)));

for (const metricName of allMetricKeys) {
  const slot = metrics.get(metricName);
  if (!slot) continue;
  const Ns = [...slot.byN.keys()].sort((a, b) => a - b);
  html += `<h2>${escapeHtml(metricName)}</h2>`;
  const headerCells = data.map(d => `<th class="num">${escapeHtml(d.scn)}</th>`).join('');
  const deltaHeader = data.length >= 2 ? '<th class="num">Δ%</th>' : '';
  html += `<table><thead><tr><th class="num">N</th>${headerCells}${deltaHeader}<th>Notes</th></tr></thead><tbody>`;
  for (const N of Ns) {
    const cells = data.map(d => {
      const cell = slot.byN.get(N)?.get(d.scn);
      return `<td class="num">${cell ? fmt(cell.value, slot.unit) : '-'}</td>`;
    }).join('');
    let delta = '';
    if (data.length >= 2) {
      const first = slot.byN.get(N)?.get(data[0].scn)?.value;
      const last  = slot.byN.get(N)?.get(data[data.length-1].scn)?.value;
      delta = deltaCell(first, last, slot.unit, metricName);
    }
    const lastNotes = slot.byN.get(N)?.get(data[data.length-1].scn)?.notes ?? '';
    html += `<tr><td class="num">${N}</td>${cells}${delta}<td class="small">${escapeHtml(lastNotes)}</td></tr>`;
  }
  html += `</tbody></table>`;
}

html += `<h2>Method</h2>
<table><tbody>
<tr><th>Stress fixture</th><td><code>frontend/e2e/perf-stress.spec.ts</code> intercepts <code>/api/jobs</code> + <code>/api/jobs/grouped</code> via <code>page.route()</code>; other endpoints fall through to dev backend.</td></tr>
<tr><th>Distribution</th><td>Bulk in 6-completed (uses full job-card template); a few in 5-human-review / 4-auto-review / 2-ready / 3-progress / 7-archive.</td></tr>
<tr><th>Initial render</th><td>Wall time from <code>page.goto('/')</code> to first <code>[data-testid="job-card"]</code> visible.</td></tr>
<tr><th>DOM count</th><td><code>document.querySelectorAll('*').length</code> after 1 s settle.</td></tr>
<tr><th>JS heap</th><td>Chrome DevTools Protocol <code>Performance.getMetrics</code> → <code>JSHeapUsedSize</code>.</td></tr>
<tr><th>Long tasks</th><td><code>PerformanceObserver({type:'longtask'})</code> aggregated over 5 s steady-state idle.</td></tr>
<tr><th>Scroll FPS</th><td>rAF-counted frames over 2 s of programmatic <code>scrollBy</code>; alternates direction so it never goes off-screen.</td></tr>
<tr><th>Run</th><td><code>RUN_PERF_BASELINE=1 PERF_SCENARIO=&lt;tag&gt; PERF_RUN_TAG=&lt;tag&gt;-runN PERF_RESET=1 npx playwright test e2e/perf-stress.spec.ts --project=chromium --workers=1</code></td></tr>
</tbody></table>
</main></body></html>`;

if (!outPath) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  outPath = path.join(perfDir, `perf-stress-${data.map(d=>d.scn).join('-vs-')}-${stamp}.html`);
}
fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, html);
console.log(`Wrote ${outPath}`);
