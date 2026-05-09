#!/usr/bin/env node
// Reads the JSON/JSONL artifacts under logs/perf/ and emits a single
// dark-themed HTML report. Supports a single scenario (Vorher report) or
// before/after comparison (Vorher + Nachher).
//
// Usage:
//   node tools/perf-report/generate.mjs --scenarios baseline [--scenarios after-cycle-1 ...] [--out <path>]
//
// Output defaults to logs/perf/perf-report-<timestamp>.html.
//
// Inputs per scenario tag <scn>:
//   logs/perf/backend-<scn>-latest.json   (xUnit perf baseline test output)
//   logs/perf/live-<scn>-latest.json      (HTTP roundtrip measurements)
//   logs/perf/frontend-<scn>-*.jsonl      (Playwright per-test JSONL append)
// Missing files are tolerated; the relevant section just stays empty.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const perfDir = path.join(repoRoot, 'logs', 'perf');

// ---------- arg parsing ----------
const args = process.argv.slice(2);
const scenarios = [];
let outPath = null;
let title = 'Agent Software Studio Performance Report';
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === '--scenarios') { scenarios.push(args[++i]); }
  else if (a === '--out')   { outPath = args[++i]; }
  else if (a === '--title') { title = args[++i]; }
}
if (scenarios.length === 0) scenarios.push('baseline');

// ---------- helpers ----------
function readJson(p) {
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return null; }
}
function readJsonl(p) {
  if (!fs.existsSync(p)) return [];
  return fs.readFileSync(p, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l));
}
function quantile(sorted, q) {
  if (sorted.length === 0) return 0;
  if (sorted.length === 1) return sorted[0];
  const pos = q * (sorted.length - 1);
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  if (lo === hi) return sorted[lo];
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}
function statsFromSamples(samples) {
  const sorted = [...samples].sort((a, b) => a - b);
  const sum = sorted.reduce((a, b) => a + b, 0);
  return {
    iterations: samples.length,
    min: sorted[0] ?? 0,
    p50: quantile(sorted, 0.5),
    p95: quantile(sorted, 0.95),
    p99: quantile(sorted, 0.99),
    max: sorted[sorted.length - 1] ?? 0,
    mean: samples.length > 0 ? sum / samples.length : 0,
  };
}
function fmt(v, suffix = 'ms') {
  if (v === null || v === undefined) return '-';
  if (typeof v !== 'number') return String(v);
  if (suffix === 'bytes') {
    if (v >= 1024*1024) return (v/1024/1024).toFixed(1) + ' MB';
    if (v >= 1024)      return (v/1024).toFixed(1) + ' KB';
    return v + ' B';
  }
  if (suffix === 'count') return Math.round(v).toString();
  return v < 10 ? v.toFixed(1) + 'ms' : Math.round(v) + 'ms';
}
function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
}
function trafficClass(ms, target = 50) {
  if (ms < target) return 'ok';
  if (ms < target * 4) return 'warn';
  return 'bad';
}

// ---------- load all scenarios ----------
function loadScenario(scn) {
  const backend = readJson(path.join(perfDir, `backend-${scn}-latest.json`));
  const live    = readJson(path.join(perfDir, `live-${scn}-latest.json`));
  // Frontend: latest JSONL by mtime for this scenario.
  const jsonls = fs.existsSync(perfDir)
    ? fs.readdirSync(perfDir).filter(f => f.startsWith(`frontend-${scn}-`) && f.endsWith('.jsonl'))
    : [];
  jsonls.sort((a, b) => fs.statSync(path.join(perfDir, b)).mtimeMs - fs.statSync(path.join(perfDir, a)).mtimeMs);
  const frontend = jsonls.length > 0 ? readJsonl(path.join(perfDir, jsonls[0])) : [];
  return { scn, backend, live, frontend };
}
const data = scenarios.map(loadScenario);
const primary = data[0];

// ---------- HTML helpers ----------
const css = `
  :root { color-scheme: dark; --bg:#111318; --panel:#181c24; --panel-2:#202633; --text:#eceff4;
          --muted:#aab2c0; --line:#344052; --ok:#73c991; --warn:#e6c07b; --bad:#ef767a; --info:#7fb4ff; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.55 "Segoe UI",system-ui,sans-serif; }
  main { max-width:1280px; margin:0 auto; padding:36px 24px 64px; }
  h1,h2,h3 { line-height:1.2; margin:0; }
  h1 { font-size:30px; }
  h2 { font-size:21px; margin-top:34px; border-top:1px solid var(--line); padding-top:24px; }
  h3 { font-size:16px; margin-top:22px; color:var(--muted); }
  p { margin:10px 0; } code { background:#0d1016; border:1px solid var(--line); border-radius:4px; padding:1px 5px; }
  .lead { color:var(--muted); font-size:16px; max-width:920px; }
  .grid { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:12px; margin-top:18px; }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:16px; }
  .card strong { display:block; font-size:24px; }
  .card span { color:var(--muted); font-size:13px; }
  .verdict { background:linear-gradient(90deg,rgba(127,180,255,.16),rgba(115,201,145,.10));
             border:1px solid rgba(127,180,255,.45); border-radius:8px; padding:18px; margin-top:22px; }
  table { width:100%; border-collapse:collapse; margin-top:12px; background:var(--panel);
          border:1px solid var(--line); border-radius:8px; overflow:hidden; }
  th,td { text-align:left; padding:10px 12px; border-bottom:1px solid var(--line); vertical-align:top; }
  th { background:var(--panel-2); color:var(--muted); font-weight:600; }
  tr:last-child td { border-bottom:0; }
  .ok { color:var(--ok); } .warn { color:var(--warn); } .bad { color:var(--bad); } .info { color:var(--info); }
  .num { text-align:right; font-variant-numeric:tabular-nums; }
  .small { color:var(--muted); font-size:13px; }
  .tag { display:inline-block; border:1px solid var(--line); border-radius:999px; padding:2px 9px;
         margin-right:6px; color:var(--muted); font-size:12px; }
  .delta-better { color:var(--ok); font-weight:600; }
  .delta-worse  { color:var(--bad); font-weight:600; }
  .delta-flat   { color:var(--muted); }
  details > summary { cursor:pointer; font-weight:600; padding:8px 0; }
`;

function renderScenarioMeta(d) {
  const b = d.backend || d.live || { generatedAt:'?', gitHead:'?', gitBranch:'?', machineCpu:'?', machineOs:'?', logicalCores:'?' };
  const totalJobs = d.live?.boardSize?.totalJobs;
  const projects = d.live?.boardSize?.projects;
  return `<table><tbody>
    <tr><th>Scenario</th><td><code>${escapeHtml(d.scn)}</code></td></tr>
    <tr><th>Generated</th><td>${escapeHtml((d.live?.generatedAt || d.backend?.generatedAt || '').replace('T', ' ').slice(0,19))} UTC</td></tr>
    <tr><th>Git</th><td>${escapeHtml(b.gitBranch||'?')} @ ${escapeHtml((b.gitHead||'?').slice(0,12))}</td></tr>
    <tr><th>Machine</th><td>${escapeHtml(b.machineCpu||'?')} (${b.logicalCores||'?'} cores) - ${escapeHtml(b.machineOs||'?')}</td></tr>
    ${totalJobs!==undefined?`<tr><th>Workload</th><td>${totalJobs} jobs across [${(projects||[]).map(escapeHtml).join(', ')}]</td></tr>`:''}
  </tbody></table>`;
}

// ---------- Live API table ----------
function renderLiveApi(d) {
  if (!d.live || !d.live.endpoints) return '<p class="small">No live API measurements for this scenario.</p>';
  const rows = d.live.endpoints.map(ep => {
    const s = ep.stats || {};
    const p50 = s.p50Ms ?? s.p50; const p95 = s.p95Ms ?? s.p95;
    const p99 = s.p99Ms ?? s.p99; const min = s.minMs ?? s.min; const max = s.maxMs ?? s.max;
    const cls = trafficClass(p95 || 0);
    return `<tr>
      <td><code>${escapeHtml(ep.name)}</code></td>
      <td class="num">${fmt(min)}</td>
      <td class="num ${trafficClass(p50)}">${fmt(p50)}</td>
      <td class="num ${cls}">${fmt(p95)}</td>
      <td class="num">${fmt(p99)}</td>
      <td class="num">${fmt(max)}</td>
    </tr>`;
  });
  return `<table><thead><tr>
    <th>Endpoint</th><th class="num">min</th><th class="num">p50</th><th class="num">p95</th><th class="num">p99</th><th class="num">max</th>
  </tr></thead><tbody>${rows.join('')}</tbody></table>`;
}

// Comparison table when multiple scenarios are present.
function renderLiveApiCompare(scenariosData) {
  // Build endpoint -> { scn -> stats }
  const map = new Map();
  for (const d of scenariosData) {
    if (!d.live?.endpoints) continue;
    for (const ep of d.live.endpoints) {
      if (!map.has(ep.name)) map.set(ep.name, { name: ep.name, byScn: {} });
      map.get(ep.name).byScn[d.scn] = ep.stats || {};
    }
  }
  const headerCells = scenariosData.map(d => `<th class="num" colspan="2">${escapeHtml(d.scn)}<br><span class="small">p50 / p95</span></th>`).join('');
  const deltaHeader = scenariosData.length >= 2 ? '<th class="num">Δ p95</th>' : '';
  const rows = [];
  for (const { name, byScn } of map.values()) {
    const cells = scenariosData.map(d => {
      const s = byScn[d.scn] || {};
      const p50 = s.p50Ms ?? s.p50; const p95 = s.p95Ms ?? s.p95;
      return `<td class="num ${trafficClass(p50||0)}">${fmt(p50)}</td><td class="num ${trafficClass(p95||0)}">${fmt(p95)}</td>`;
    }).join('');
    let delta = '';
    if (scenariosData.length >= 2) {
      const a = byScn[scenariosData[0].scn]?.p95Ms ?? byScn[scenariosData[0].scn]?.p95;
      const b = byScn[scenariosData[scenariosData.length - 1].scn]?.p95Ms ?? byScn[scenariosData[scenariosData.length - 1].scn]?.p95;
      if (typeof a === 'number' && typeof b === 'number' && a > 0) {
        const change = ((b - a) / a) * 100;
        const cls = change < -5 ? 'delta-better' : change > 5 ? 'delta-worse' : 'delta-flat';
        const sign = change >= 0 ? '+' : '';
        delta = `<td class="num ${cls}">${sign}${change.toFixed(0)}%</td>`;
      } else delta = '<td class="num delta-flat">-</td>';
    }
    rows.push(`<tr><td><code>${escapeHtml(name)}</code></td>${cells}${delta}</tr>`);
  }
  return `<table><thead><tr><th>Endpoint</th>${headerCells}${deltaHeader}</tr></thead><tbody>${rows.join('')}</tbody></table>`;
}

// ---------- Backend in-process table ----------
function renderBackend(d) {
  if (!d.backend || !d.backend.backend) return '<p class="small">No backend in-process measurements for this scenario.</p>';
  // Group by metric name; rows per N (jobCount).
  const byMetric = new Map();
  for (const m of d.backend.backend) {
    if (!byMetric.has(m.name)) byMetric.set(m.name, []);
    byMetric.get(m.name).push(m);
  }
  const rows = [];
  for (const [name, metrics] of byMetric) {
    metrics.sort((a, b) => a.jobCount - b.jobCount);
    for (const m of metrics) {
      const s = m.stats || {};
      rows.push(`<tr>
        <td><code>${escapeHtml(name)}</code></td>
        <td class="num">${m.jobCount}</td>
        <td class="num ${trafficClass(s.p50Ms||0,5)}">${fmt(s.p50Ms)}</td>
        <td class="num ${trafficClass(s.p95Ms||0,10)}">${fmt(s.p95Ms)}</td>
        <td class="num">${fmt(s.p99Ms)}</td>
        <td class="num">${fmt(s.maxMs)}</td>
      </tr>`);
    }
  }
  return `<table><thead><tr>
    <th>Hot path</th><th class="num">N jobs</th><th class="num">p50</th><th class="num">p95</th><th class="num">p99</th><th class="num">max</th>
  </tr></thead><tbody>${rows.join('')}</tbody></table>`;
}

// ---------- Frontend table ----------
function renderFrontend(d) {
  if (!d.frontend || d.frontend.length === 0) return '<p class="small">No frontend measurements for this scenario.</p>';
  const rows = d.frontend.map(m => {
    const s = statsFromSamples(m.samples || []);
    const isMs = m.unit === 'ms';
    const isCount = m.unit === 'count';
    const isBytes = m.unit === 'bytes';
    const fmtVal = v => isMs ? fmt(v) : (isCount ? fmt(v,'count') : (isBytes ? fmt(v,'bytes') : String(v)));
    const cls = isMs ? trafficClass(s.p95) : '';
    return `<tr>
      <td>${escapeHtml(m.surface)}</td>
      <td>${escapeHtml(m.metric)}</td>
      <td class="num">${m.iterations}</td>
      <td class="num">${fmtVal(s.min)}</td>
      <td class="num ${isMs?trafficClass(s.p50):''}">${fmtVal(s.p50)}</td>
      <td class="num ${cls}">${fmtVal(s.p95)}</td>
      <td class="num">${fmtVal(s.max)}</td>
      <td class="small">${escapeHtml(m.notes||'')}</td>
    </tr>`;
  });
  return `<table><thead><tr>
    <th>Surface</th><th>Metric</th><th class="num">n</th><th class="num">min</th><th class="num">p50</th><th class="num">p95</th><th class="num">max</th><th>Notes</th>
  </tr></thead><tbody>${rows.join('')}</tbody></table>`;
}

// Front-end comparison aggregates samples from multiple scenarios.
function renderFrontendCompare(scenariosData) {
  // Build (surface,metric) -> { scn -> stats }
  const map = new Map();
  for (const d of scenariosData) {
    for (const m of d.frontend || []) {
      const key = `${m.surface}::${m.metric}`;
      if (!map.has(key)) map.set(key, { surface: m.surface, metric: m.metric, unit: m.unit, byScn: {}, notes: m.notes });
      map.get(key).byScn[d.scn] = statsFromSamples(m.samples || []);
    }
  }
  const headerCells = scenariosData.map(d => `<th class="num" colspan="2">${escapeHtml(d.scn)}<br><span class="small">p50 / p95</span></th>`).join('');
  const deltaHeader = scenariosData.length >= 2 ? '<th class="num">Δ p95</th>' : '';
  const rows = [];
  for (const { surface, metric, unit, byScn, notes } of map.values()) {
    const isMs = unit === 'ms';
    const isCount = unit === 'count';
    const isBytes = unit === 'bytes';
    const fmtVal = v => isMs ? fmt(v) : (isCount ? fmt(v,'count') : (isBytes ? fmt(v,'bytes') : String(v)));
    const cells = scenariosData.map(d => {
      const s = byScn[d.scn] || {};
      const cls = isMs ? trafficClass(s.p95||0) : '';
      return `<td class="num ${isMs?trafficClass(s.p50||0):''}">${fmtVal(s.p50||0)}</td><td class="num ${cls}">${fmtVal(s.p95||0)}</td>`;
    }).join('');
    let delta = '';
    if (scenariosData.length >= 2) {
      const a = byScn[scenariosData[0].scn]?.p95;
      const b = byScn[scenariosData[scenariosData.length-1].scn]?.p95;
      if (typeof a === 'number' && typeof b === 'number' && a > 0) {
        const change = ((b - a) / a) * 100;
        const cls = change < -5 ? 'delta-better' : change > 5 ? 'delta-worse' : 'delta-flat';
        const sign = change >= 0 ? '+' : '';
        delta = `<td class="num ${cls}">${sign}${change.toFixed(0)}%</td>`;
      } else delta = '<td class="num delta-flat">-</td>';
    }
    rows.push(`<tr><td>${escapeHtml(surface)}</td><td>${escapeHtml(metric)}</td>${cells}${delta}<td class="small">${escapeHtml(notes||'')}</td></tr>`);
  }
  return `<table><thead><tr>
    <th>Surface</th><th>Metric</th>${headerCells}${deltaHeader}<th>Notes</th>
  </tr></thead><tbody>${rows.join('')}</tbody></table>`;
}

// ---------- Top offenders summary ----------
function renderHotspots(d) {
  if (!d.live?.endpoints) return '';
  const p95Of = ep => (ep.stats?.p95Ms ?? ep.stats?.p95) || 0;
  const sorted = [...d.live.endpoints].sort((a, b) => p95Of(b) - p95Of(a)).slice(0, 5);
  const items = sorted.map(ep => {
    const p95 = p95Of(ep);
    return `<div class="card">
      <strong class="${trafficClass(p95)}">${fmt(p95)}</strong>
      <span>${escapeHtml(ep.name)}<br>p95 (live HTTP)</span>
    </div>`;
  });
  return `<div class="grid">${items.join('')}</div>`;
}

// ---------- Build HTML ----------
const isCompare = data.length > 1;
const sections = [];

sections.push(`<h2>Run metadata</h2>`);
for (const d of data) sections.push(`<h3>${escapeHtml(d.scn)}</h3>${renderScenarioMeta(d)}`);

sections.push(`<h2>Top offenders (live HTTP, ${escapeHtml(primary.scn)})</h2>${renderHotspots(primary)}`);

// User targets / acceptance bar for the perf overhaul.
sections.push(`<h2>Targets &amp; verdict</h2>`);
const targetRows = [
  ['/api/runner/status p95',         '50ms',  primary.live?.endpoints?.find(e => e.name.includes('runner/status'))?.stats],
  ['/api/jobs/grouped p95',          '100ms', primary.live?.endpoints?.find(e => e.name.includes('jobs/grouped'))?.stats],
  ['/api/jobs p95',                  '100ms', primary.live?.endpoints?.find(e => e.name === 'GET /api/jobs')?.stats],
  ['/api/jobs/{id} p95',             '50ms',  primary.live?.endpoints?.find(e => e.name === 'GET /api/jobs/{id}')?.stats],
  ['/api/jobs/{id}/output p95',      '50ms',  primary.live?.endpoints?.find(e => e.name.includes('output'))?.stats],
  ['/api/jobs/{id}/runs p95',        '50ms',  primary.live?.endpoints?.find(e => e.name.includes('/runs'))?.stats],
  ['/api/cli/usage p95',             '50ms',  primary.live?.endpoints?.find(e => e.name.includes('cli/usage'))?.stats],
];
const targetTrs = targetRows.map(([label, target, st]) => {
  const cur = (st?.p95Ms ?? st?.p95);
  const ok = typeof cur === 'number' && cur < parseFloat(target);
  const cls = ok ? 'ok' : (cur < parseFloat(target) * 4 ? 'warn' : 'bad');
  return `<tr><td>${escapeHtml(label)}</td><td class="num">${escapeHtml(target)}</td><td class="num ${cls}">${fmt(cur)}</td><td class="${cls}">${ok ? 'PASS' : 'MISS'}</td></tr>`;
});
sections.push(`<table><thead><tr><th>Endpoint metric</th><th class="num">Target</th><th class="num">Current</th><th>Status</th></tr></thead><tbody>${targetTrs.join('')}</tbody></table>`);
sections.push(`<p class="small">Targets are the user's "double-digit ms locally" bar applied to the polled endpoints. /api/jobs/grouped and /api/jobs allow 100ms because they materialize the full board; the rest should sit below 50ms with a healthy projection cache.</p>`);

sections.push(`<h2>Live HTTP API ${isCompare ? '(before/after)' : '(p50 / p95)'}</h2>`);
sections.push(`<p class="small">Wall-clock from a Node fetch loop, including network and JSON serialize. ` +
  `Color: <span class="ok">&lt;50ms</span>, <span class="warn">50-200ms</span>, <span class="bad">≥200ms</span>.</p>`);
sections.push(isCompare ? renderLiveApiCompare(data) : renderLiveApi(primary));

sections.push(`<h2>Backend hot paths (in-process, scaling by N jobs)</h2>`);
sections.push(`<p class="small">Direct method invocation, no HTTP. Useful to see algorithmic complexity (linear vs. constant).</p>`);
for (const d of data) {
  if (data.length > 1) sections.push(`<h3>${escapeHtml(d.scn)}</h3>`);
  sections.push(renderBackend(d));
}

sections.push(`<h2>Frontend per-surface ${isCompare ? '(before/after)' : ''}</h2>`);
sections.push(`<p class="small">Playwright in headless Chromium against the dev frontend. ` +
  `Roundtrip = browser-side fetch (HttpClient stack overhead included). ` +
  `Idle 10s = main-thread Long Tasks (>50ms blocks) over a 10-second window with no user interaction.</p>`);
sections.push(isCompare ? renderFrontendCompare(data) : renderFrontend(primary));

const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title><style>${css}</style></head>
<body><main>
<h1>${escapeHtml(title)}</h1>
<p class="lead">Generated ${new Date().toISOString().replace('T',' ').slice(0,19)} UTC by <code>tools/perf-report/generate.mjs</code>.
Numbers are reproducible: run <code>RUN_PERF_BASELINE=1 PERF_SCENARIO=&lt;tag&gt; dotnet test --filter BackendBaselineTests</code>,
<code>node tools/perf-report/measure-live-api.mjs &lt;tag&gt;</code>, and
<code>RUN_PERF_BASELINE=1 PERF_SCENARIO=&lt;tag&gt; npx playwright test e2e/perf-baseline.spec.ts</code>,
then re-run this generator with <code>--scenarios &lt;tag1&gt; --scenarios &lt;tag2&gt;</code>.</p>
<div class="verdict">
<strong>Scenarios in this report:</strong> ${data.map(d => `<span class="tag">${escapeHtml(d.scn)}</span>`).join('')}
${isCompare ? '<br><span class="small">Δ columns are computed last-vs-first, so a negative percentage means the second scenario is faster.</span>' : ''}
</div>
${sections.join('\n')}
<h2>Method &amp; reproducibility</h2>
<table><thead><tr><th>Layer</th><th>Tool</th><th>Source</th></tr></thead><tbody>
<tr><td>Backend hot paths (in-process)</td><td>xUnit + <code>System.Diagnostics.Stopwatch</code></td><td><code>backend.Tests/PerfBaseline/BackendBaselineTests.cs</code></td></tr>
<tr><td>Live HTTP API</td><td>Node <code>fetch</code> + <code>performance.now()</code></td><td><code>tools/perf-report/measure-live-api.mjs</code></td></tr>
<tr><td>Frontend (browser-side)</td><td>Playwright + <code>PerformanceObserver longtask</code></td><td><code>frontend/e2e/perf-baseline.spec.ts</code> + <code>frontend/e2e/helpers/timing.ts</code></td></tr>
<tr><td>Report</td><td>Plain Node + handwritten HTML</td><td><code>tools/perf-report/generate.mjs</code></td></tr>
</tbody></table>
<p class="small">Each measurement runs warm-ups before the recorded iterations. Backend in-process is 30 iterations after 3 warm-ups; live HTTP defaults to 30 iterations after 3 warm-ups; Playwright defaults to 10 iterations.</p>
</main></body></html>`;

if (!outPath) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  outPath = path.join(perfDir, `perf-report-${data.map(d=>d.scn).join('-vs-')}-${stamp}.html`);
}
fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, html);
console.log(`Wrote ${outPath}`);
