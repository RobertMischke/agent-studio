/**
 * Visual proof for the "header buttons consistent size + remove settings
 * gear" cleanup. The spec renders two minimal copies of the header chrome
 * via page.setContent so the screenshots stay reproducible without
 * touching live data or relying on the dev server being up:
 *
 *  - results/header-before.png — pre-cleanup heights/paddings (the
 *    geometry that shipped before this commit) + the redundant project
 *    settings gear button next to each project chip.
 *  - results/header-after.png — unified 28 px height, consistent
 *    padding, gear removed; project settings still reachable via the
 *    project page button (📊), which opens the Project Window where
 *    the Settings rail now lives.
 *
 * The CSS in each fragment is copy-pasted directly from app.scss / the
 * matching component scss files so the screenshots reflect the actual
 * compiled output. The spec is purely visual; it does not exercise app
 * behaviour, so it has no @billable cost and does not need a backend.
 */
import { test } from '@playwright/test';
import { existsSync, mkdirSync } from 'node:fs';
import * as path from 'node:path';

const JOB_RESULTS = path.resolve(
  __dirname,
  '..',
  '..',
  '..',
  '..',
  'agent-taskboard-workspace',
  'projects',
  'agent-taskboard',
  '3-progress',
  'cleanup-header-button-sizes-and-remove-settings-gear',
  'results',
);

function ensureResultsDir(): string {
  if (!existsSync(JOB_RESULTS)) mkdirSync(JOB_RESULTS, { recursive: true });
  return JOB_RESULTS;
}

const SHARED_BODY = `
  <style>
    body { margin: 0; background: #0f0f1a; font-family: 'Segoe UI', system-ui, sans-serif; padding: 12px; }
    .frame-label { color: #94a3b8; font-size: 12px; margin: 12px 0 6px; letter-spacing: 0.04em; text-transform: uppercase; font-weight: 700; }
  </style>
`;

const BEFORE_HTML = `
<!doctype html><html><head><meta charset="utf-8" />${SHARED_BODY}
<style>
  .header { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 4px 12px; background: #181825; border-bottom: 1px solid rgba(255,255,255,0.06); min-height: 36px; color: #e2e8f0; }
  .brand { display: flex; align-items: center; gap: 8px; font-weight: 700; font-size: 13px; }
  .filters { display: flex; gap: 6px; align-items: center; }
  .filter-chip { display: inline-flex; align-items: center; gap: 6px; background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.20); color: #e2e8f0; padding: 4px 12px 4px 4px; border-radius: 20px; font-size: 12px; font-weight: 600; }
  .filter-chip__disk { display: inline-grid; place-items: center; width: 18px; height: 18px; border-radius: 999px; background: #8b5cf6; color: #0b1020; font-size: 11px; font-weight: 800; }
  .auto-toggle { display: inline-flex; align-items: center; gap: 4px; background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.12); color: #94a3b8; padding: 4px 10px; border-radius: 16px; font-size: 11px; font-weight: 600; }
  .icon-btn-26 { background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.10); color: rgba(255,255,255,0.55); border-radius: 6px; width: 26px; height: 26px; font-size: 14px; padding: 0; }
  .actions { display: flex; gap: 6px; align-items: center; }
  .auto-review { display: inline-flex; align-items: center; gap: 6px; height: 26px; padding: 0 9px; border-radius: 6px; border: 1px solid rgba(245,158,11,0.36); background: rgba(245,158,11,0.12); color: #fde68a; font-size: 12px; font-weight: 650; }
  .auto-review__dot { width: 7px; height: 7px; border-radius: 999px; background: currentColor; }
  .version-badge { display: inline-flex; align-items: center; gap: 0.4rem; padding: 0.2rem 0.55rem; border-radius: 4px; border: 1px solid rgba(255,255,255,0.12); background: rgba(255,255,255,0.04); color: rgba(205,214,244,0.85); font-size: 0.75rem; font-family: ui-monospace, monospace; line-height: 1; }
  .client-filter { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; color: #cbd5e1; padding: 0 6px; }
  .client-filter__select { background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.10); color: #e2e8f0; border-radius: 6px; padding: 4px 8px; font-size: 12px; }
  .filters-trigger { display: inline-flex; align-items: center; gap: 6px; background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.12); color: #cbd5e1; padding: 4px 10px; border-radius: 6px; font-size: 12px; font-weight: 600; }
  .filter-search { display: inline-flex; align-items: center; gap: 6px; padding: 4px 10px; background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.10); border-radius: 6px; color: #cbd5e1; font-size: 12px; }
  .btn { background: rgba(255,255,255,0.10); border: 1px solid rgba(255,255,255,0.20); color: #f8fafc; padding: 6px 14px; border-radius: 6px; font-size: 12px; font-weight: 600; }
  .btn--create { background: rgba(139,92,246,0.45); border-color: rgba(167,139,250,0.85); color: #ffffff; }
  .btn--compact { display: inline-flex; align-items: center; gap: 6px; }
  .devtools-trigger { background: transparent; border: 1px solid rgba(255,255,255,0.10); color: #94a3b8; width: 28px; height: 28px; border-radius: 6px; font-size: 18px; padding: 0; display: grid; place-items: center; }
</style></head>
<body>
  <div class="frame-label">BEFORE — mixed heights, redundant project settings gear (⚙)</div>
  <header class="header">
    <div class="brand">🟣 Agent Software Studio</div>
    <div class="filters">
      <div style="display:inline-flex;align-items:center;gap:4px;">
        <button class="filter-chip"><span class="filter-chip__disk">A</span>Agent Software Studio</button>
        <button class="auto-toggle">🔁 Auto</button>
        <button class="icon-btn-26">📊</button>
        <button class="icon-btn-26">⚙</button>
      </div>
      <div style="display:inline-flex;align-items:center;gap:4px;">
        <button class="filter-chip"><span class="filter-chip__disk">R</span>Runbook</button>
        <button class="auto-toggle">▶ Auto</button>
        <button class="icon-btn-26">📊</button>
        <button class="icon-btn-26">⚙</button>
      </div>
    </div>
    <div class="actions">
      <span class="auto-review"><span class="auto-review__dot"></span>Auto-review idle</span>
      <button class="version-badge"><span>v0.42</span><span style="opacity:.6">abc123</span></button>
      <label class="client-filter"><span>Owner:</span><select class="client-filter__select"><option>All</option></select></label>
      <button class="filters-trigger">▾ Filters</button>
      <button class="filter-search">🔍</button>
      <button class="btn btn--compact">▥ Full</button>
      <button class="btn btn--create">＋ Add Task</button>
      <button class="devtools-trigger">⋮</button>
    </div>
  </header>
</body></html>`;

const AFTER_HTML = `
<!doctype html><html><head><meta charset="utf-8" />${SHARED_BODY}
<style>
  .header {
    --header-btn-height: 28px;
    --header-btn-radius: 6px;
    --header-btn-padding-x: 10px;
    --header-btn-gap: 6px;
    display: flex; justify-content: space-between; align-items: center; gap: 12px;
    padding: 4px 12px; background: #181825; border-bottom: 1px solid rgba(255,255,255,0.06);
    min-height: 40px; color: #e2e8f0;
  }
  .brand { display: flex; align-items: center; gap: 8px; font-weight: 700; font-size: 13px; }
  .filters { display: flex; gap: 6px; align-items: center; }
  .filter-chip {
    display: inline-flex; align-items: center; gap: var(--header-btn-gap);
    background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.20); color: #e2e8f0;
    height: var(--header-btn-height); padding: 0 12px 0 4px;
    border-radius: calc(var(--header-btn-height) / 2);
    font-size: 12px; font-weight: 600; line-height: 1; box-sizing: border-box;
  }
  .filter-chip__disk { display: inline-grid; place-items: center; width: 18px; height: 18px; border-radius: 999px; background: #8b5cf6; color: #0b1020; font-size: 11px; font-weight: 800; }
  .auto-toggle {
    display: inline-flex; align-items: center; gap: 4px;
    background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.12); color: #94a3b8;
    height: var(--header-btn-height); padding: 0 var(--header-btn-padding-x);
    border-radius: calc(var(--header-btn-height) / 2);
    font-size: 11px; font-weight: 600; line-height: 1; box-sizing: border-box;
  }
  .header-btn {
    display: inline-flex; align-items: center; gap: var(--header-btn-gap);
    height: var(--header-btn-height); padding: 0 var(--header-btn-padding-x);
    border-radius: var(--header-btn-radius);
    border: 1px solid rgba(255,255,255,0.12); background: rgba(255,255,255,0.04); color: #cbd5e1;
    font-size: 12px; font-weight: 600; line-height: 1; box-sizing: border-box;
  }
  .header-btn--icon { width: var(--header-btn-height); padding: 0; justify-content: center; }
  .project-tab__shell { color: rgba(255,255,255,0.55); font-size: 14px; }
  .actions { display: flex; gap: 6px; align-items: center; }
  .auto-review {
    display: inline-flex; align-items: center; gap: var(--header-btn-gap);
    height: var(--header-btn-height); padding: 0 var(--header-btn-padding-x);
    border-radius: var(--header-btn-radius);
    border: 1px solid rgba(245,158,11,0.36); background: rgba(245,158,11,0.12); color: #fde68a;
    font-size: 12px; font-weight: 650; line-height: 1; box-sizing: border-box;
  }
  .auto-review__dot { width: 7px; height: 7px; border-radius: 999px; background: currentColor; }
  .version-badge {
    display: inline-flex; align-items: center; gap: var(--header-btn-gap);
    height: var(--header-btn-height); padding: 0 var(--header-btn-padding-x);
    border-radius: var(--header-btn-radius);
    border: 1px solid rgba(255,255,255,0.12); background: rgba(255,255,255,0.04); color: rgba(205,214,244,0.85);
    font-size: 0.75rem; font-family: ui-monospace, monospace; line-height: 1; box-sizing: border-box;
  }
  .client-filter { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; color: #cbd5e1; padding: 0 6px; }
  .client-filter__select {
    background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.10); color: #e2e8f0;
    border-radius: var(--header-btn-radius); height: var(--header-btn-height);
    padding: 0 8px; font-size: 12px; line-height: 1; box-sizing: border-box;
  }
  .filters-trigger {
    display: inline-flex; align-items: center; gap: var(--header-btn-gap);
    background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.12); color: #cbd5e1;
    height: var(--header-btn-height); padding: 0 var(--header-btn-padding-x);
    border-radius: var(--header-btn-radius);
    font-size: 12px; font-weight: 600; line-height: 1; box-sizing: border-box;
  }
  .filter-search {
    display: inline-flex; align-items: center; gap: var(--header-btn-gap);
    height: var(--header-btn-height); padding: 0 var(--header-btn-padding-x);
    background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.10);
    border-radius: var(--header-btn-radius); color: #cbd5e1; font-size: 12px; line-height: 1; box-sizing: border-box;
  }
  .btn {
    display: inline-flex; align-items: center; justify-content: center; gap: var(--header-btn-gap);
    background: rgba(255,255,255,0.10); border: 1px solid rgba(255,255,255,0.20); color: #f8fafc;
    height: var(--header-btn-height); padding: 0 14px;
    border-radius: var(--header-btn-radius);
    font-size: 12px; font-weight: 600; line-height: 1; box-sizing: border-box;
  }
  .btn--create { background: rgba(139,92,246,0.45); border-color: rgba(167,139,250,0.85); color: #ffffff; }
  .devtools-trigger {
    background: transparent; border: 1px solid rgba(255,255,255,0.10); color: #94a3b8;
    width: var(--header-btn-height); height: var(--header-btn-height); border-radius: var(--header-btn-radius);
    font-size: 18px; padding: 0; display: grid; place-items: center; box-sizing: border-box;
  }
</style></head>
<body>
  <div class="frame-label">AFTER — unified 28 px row, gear removed (📊 still opens the Project Window)</div>
  <header class="header">
    <div class="brand">🟣 Agent Software Studio</div>
    <div class="filters">
      <div style="display:inline-flex;align-items:center;gap:4px;">
        <button class="filter-chip"><span class="filter-chip__disk">A</span>Agent Software Studio</button>
        <button class="auto-toggle">🔁 Auto</button>
        <button class="header-btn header-btn--icon project-tab__shell">📊</button>
      </div>
      <div style="display:inline-flex;align-items:center;gap:4px;">
        <button class="filter-chip"><span class="filter-chip__disk">R</span>Runbook</button>
        <button class="auto-toggle">▶ Auto</button>
        <button class="header-btn header-btn--icon project-tab__shell">📊</button>
      </div>
    </div>
    <div class="actions">
      <span class="auto-review"><span class="auto-review__dot"></span>Auto-review idle</span>
      <button class="version-badge"><span>v0.42</span><span style="opacity:.6">abc123</span></button>
      <label class="client-filter"><span>Owner:</span><select class="client-filter__select"><option>All</option></select></label>
      <button class="filters-trigger">▾ Filters</button>
      <button class="filter-search">🔍</button>
      <button class="btn">▥ Full</button>
      <button class="btn btn--create">＋ Add Task</button>
      <button class="devtools-trigger">⋮</button>
    </div>
  </header>
</body></html>`;

test.describe('header cleanup — visual snapshot', () => {
  test('captures before / after of the header bar', async ({ page }) => {
    const out = ensureResultsDir();
    await page.setViewportSize({ width: 1600, height: 200 });

    await page.setContent(BEFORE_HTML);
    await page.locator('header.header').screenshot({ path: path.join(out, 'header-before.png') });

    await page.setContent(AFTER_HTML);
    await page.locator('header.header').screenshot({ path: path.join(out, 'header-after.png') });

    // Side-by-side for the PR description.
    const sideBySide = `<!doctype html><html><head><meta charset="utf-8" />
      <style>body{margin:0;background:#0f0f1a;font-family:'Segoe UI';padding:0;}</style>
      </head><body>${BEFORE_HTML.slice(BEFORE_HTML.indexOf('<body>') + '<body>'.length, BEFORE_HTML.indexOf('</body>'))}
      ${AFTER_HTML.slice(AFTER_HTML.indexOf('<body>') + '<body>'.length, AFTER_HTML.indexOf('</body>'))}
      </body></html>`;
    await page.setContent(sideBySide);
    await page.screenshot({ path: path.join(out, 'header-comparison.png'), fullPage: true });
  });
});
