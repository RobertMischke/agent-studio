// Disposable evidence-screenshot driver for ASS-1727. Renders THIS worktree's
// production build (served by _static-server.mjs) with the board boot API
// surface route-mocked, and captures the Archive lane hydrated from the paged
// GET /api/tasks/archive endpoint.
//
// This is a --mocked shot: a --real shot is not reachable from a dev job
// worktree because the running dev stack (:4010/:5030) serves the canonical
// dev checkout, not this branch, and AGENTS.md forbids bringing the dev
// backend up from a job. The committed real-backend regression tests
// (backend.Tests/TaskArchiveEndpointTests.cs, 5 tests) plus the FE archive-lane
// render spec (task-column.spec.ts) are the canonical correctness evidence.
//
// The board's grouped.archive lane is intentionally empty (the cache-backed
// board scan excludes the terminal 7-archive lane, by design), so the Archive
// column hydrates from the paged read endpoint. That is the exact behaviour the
// bug (ASS-1727: "Archive view empty despite 852 archived tasks") needed.
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { createRequire } from 'node:module';

const BASE = process.env.SHOT_BASE || 'http://127.0.0.1:4099';
// Resolve output paths relative to this file (results/) so artifacts always
// land here regardless of cwd.
const OUT_DIR = dirname(fileURLToPath(import.meta.url));
const out = (name) => join(OUT_DIR, name);
// playwright is a frontend devDependency; ESM resolves bare specifiers from the
// importing file's tree (results/, which has no node_modules), so load it via a
// require rooted at frontend/package.json instead.
const require = createRequire(join(OUT_DIR, '..', 'frontend', 'package.json'));
const { chromium } = require('playwright');

// A pool of realistic archived-task rows. Several titles contain "migration"
// so the text-filter shot has a meaningful subset to land on.
const POOL = [
  'ASS-1649 Slim-hydrate the terminal 7-archive lane',
  'ASS-1715 Isolated worktree test stack on dynamic ports',
  'ASS-1402 Postgres connection-pool migration',
  'ASS-1581 Drop legacy 6-archive folder support',
  'ASS-1390 Pipeline catalogue cost columns',
  'ASS-1244 Schema migration for run-event NDJSON frames',
  'ASS-1188 Crash-recovery landing for dead runs',
  'ASS-1097 Codex JSONL adapter completion contract',
  'ASS-1640 Board snapshot excludes archived folders',
  'ASS-1455 Gemini event adapter usage stats',
  'ASS-1322 Data migration: backfill enteredLaneAt',
  'ASS-1210 Status-bar usage caps overlay',
  'ASS-1501 Tag registry store hydration',
  'ASS-1378 Quota window reset labels',
  'ASS-1266 Workspace settings rail navigation',
  'ASS-1144 Lane collapse persistence',
];

function rowFor(i, title) {
  const day = String((i % 27) + 1).padStart(2, '0');
  return {
    id: `arch-${i}`,
    taskKey: `studio::arch-${i}`,
    key: title.split(' ')[0],
    title,
    state: '7-archive',
    projectName: 'agent-taskboard',
    watchPath: '/workspace/agent-taskboard',
    enteredLaneAt: `2026-05-${day}T09:00:00Z`,
    lastActivity: `2026-05-${day}T11:30:00Z`,
    commitCount: (i % 5) + 1,
    codeActivityDetected: i % 3 !== 0,
    taskType: ['bug', 'feature', 'chore'][i % 3],
    cliType: ['claude', 'codex', 'gemini'][i % 3],
    agent: ['claude', 'codex', 'gemini'][i % 3],
  };
}

const ALL = POOL.map((t, i) => rowFor(i, t));

const emptyGrouped = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const json = (route, body) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

// Archive endpoint mock: honour offset/limit/search like the real handler.
// `total` for the unfiltered view is reported as 852 to mirror the bug's data
// shape ("852 archived tasks"); the visible page is the seeded POOL slice.
function archiveResponse(url) {
  const u = new URL(url);
  const offset = Number(u.searchParams.get('offset') ?? '0');
  const limit = Number(u.searchParams.get('limit') ?? '50');
  const search = (u.searchParams.get('search') ?? '').trim().toLowerCase();
  let pool = ALL;
  let total;
  if (search) {
    pool = ALL.filter((r) => r.title.toLowerCase().includes(search));
    total = pool.length; // filtered: honest total so the count + empty-state are correct
  } else {
    total = 852; // unfiltered: evoke the bug's "852 archived tasks" scale
  }
  const items = pool.slice(offset, offset + limit);
  return { items, total, offset, limit };
}

async function main() {
  const browser = await chromium.launch();
  // Block the app's service worker. The production build registers
  // ngsw-worker.js (enabled when !isDevMode); once it activates a few seconds in
  // it intercepts /api/** from its own cache and bypasses Playwright's route
  // mock — so the search re-queries fired but never reached the mock, leaving the
  // lane on stale unfiltered data. Blocking the SW keeps every /api request
  // flowing through the mock deterministically.
  const context = await browser.newContext({ viewport: { width: 1500, height: 1700 }, serviceWorkers: 'block' });
  const page = await context.newPage();

  // Legacy chrome (vsCodeLayout off) renders the board dashboard directly with
  // far less surrounding shell, which keeps the Archive lane the clear subject
  // of the shot. The Archive lane + paged endpoint behaviour is identical in
  // both layouts (both bind laneGroups()).
  await page.addInitScript(() => {
    try { window.localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch {}
  });

  // SignalR hub: the app opens /hubs/jobs for live push. With no backend, abort
  // the negotiate so it fails fast instead of parsing the SPA's index.html as a
  // negotiate payload (a noisy but benign console error). Push isn't part of
  // this evidence — the archive lane hydrates over plain HTTP.
  await page.route('**/hubs/**', (route) => route.abort());

  await page.route('**/api/**', async (route) => {
    const url = route.request().url();
    if (/\/api\/tasks\/archive(\?|$)/.test(url)) {
      const r = archiveResponse(url);
      console.log(`[mock] archive -> items=${r.items.length} total=${r.total} (${url.replace(BASE, '')})`);
      return json(route, r);
    }
    if (url.includes('/api/tasks/grouped')) return json(route, emptyGrouped);
    if (/\/api\/tasks(\?|$)/.test(url)) return json(route, []);
    // RunnerStatus is a { projects: {} } map; returning [] makes the board's
    // projectRunnerIndicator read `.projects[name]` off undefined and throw in
    // CD, which freezes later view updates (e.g. the filter re-query).
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    // Empty watch-paths: the board lanes (laneGroups) render independent of
    // projects, and an empty list avoids the per-project chip/runner computeds
    // that would otherwise read off the (mocked-empty) runner-status map.
    if (url.includes('/api/watch-paths')) return json(route, []);
    if (url.includes('/api/tags')) return json(route, []);
    // The status-bar quota cluster polls /api/cli/quota and does
    // `report().snapshots.find(...)`; a bare [] is truthy-without-snapshots and
    // throws in that computed, which pops a modal error dialog over the board.
    if (url.includes('/api/cli/quota/caps')) return json(route, { defaultCapPct: 95, caps: {} });
    if (url.includes('/api/cli/quota'))
      return json(route, { at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/cli/usage')) return json(route, { at: new Date().toISOString(), sections: [] });
    if (url.includes('/api/cli/contracts')) return json(route, []);
    if (/\/api\/cli\/(claude|codex|gemini|copilot)\/models/.test(url))
      return json(route, { models: [], source: 'mocked', fetchedAt: new Date().toISOString() });
    // Benign empty default for every other boot endpoint (cli catalogs, dev
    // flags, clients, settings, pipeline catalogue, …). Lists keep computeds
    // from tripping on undefined; the few object-shaped ones degrade to their
    // service error/empty path without wedging the board render.
    return json(route, []);
  });

  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console.error: ' + m.text().slice(0, 200)); });

  await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);

  const dismissDialogs = async () => {
    for (let i = 0; i < 8; i++) {
      const overlayEl = page.locator('.dialog__overlay');
      if ((await overlayEl.count()) === 0) return;
      await overlayEl.first().click({ position: { x: 4, y: 4 }, timeout: 2000 }).catch(() => {});
      await page.keyboard.press('Escape').catch(() => {});
      await page.waitForTimeout(300);
    }
  };
  await dismissDialogs();

  const archive = page.getByTestId('lane-7-archive');
  try {
    await archive.waitFor({ state: 'visible', timeout: 20000 });
    await archive.scrollIntoViewIfNeeded().catch(() => {});
    await page.getByTestId('archive-row').first().waitFor({ state: 'visible', timeout: 20000 });
    await page.waitForTimeout(500);

    await archive.screenshot({ path: out('archive-lane-populated--mocked.png') });
    console.log('screenshot written: archive-lane-populated--mocked.png');
    console.log('archive rows visible:', await page.getByTestId('archive-row').count());

    // Text filter → subset. The component debounces (300ms) then re-queries
    // GET /api/tasks/archive?search=… from offset 0. We type real keystrokes
    // (pressSequentially) so the native `input` event reliably reaches Angular's
    // (input) binding, and wait for the matching request so the shot is
    // deterministic rather than racing a fixed timeout.
    const filterInput = page.getByTestId('archive-filter-input');
    const applySearch = async (term) => {
      const fired = page
        .waitForRequest(
          (r) => /\/api\/tasks\/archive\b/.test(r.url()) && new URL(r.url()).searchParams.get('search') === term,
          { timeout: 6000 },
        )
        .then(() => true)
        .catch(() => false);
      await filterInput.click();
      await filterInput.press('ControlOrMeta+a');
      await filterInput.pressSequentially(term, { delay: 25 });
      console.log(`[search] "${term}" -> request fired: ${await fired}, value "${await filterInput.inputValue()}"`);
      await page.waitForTimeout(400); // response → render
    };

    await applySearch('migration');
    await archive.scrollIntoViewIfNeeded().catch(() => {});
    await archive.screenshot({ path: out('archive-lane-filtered--mocked.png') });
    console.log('screenshot written: archive-lane-filtered--mocked.png');
    console.log('filtered rows visible:', await page.getByTestId('archive-row').count());

    // Genuine empty state (filter matches nothing → total 0).
    await applySearch('zzz-no-such-task');
    const gotEmpty = await page.getByTestId('archive-empty').isVisible().catch(() => false);
    console.log('archive-empty visible after no-match filter:', gotEmpty);
    await archive.scrollIntoViewIfNeeded().catch(() => {});
    await archive.screenshot({ path: out('archive-lane-empty--mocked.png') });
    console.log('screenshot written: archive-lane-empty--mocked.png');
    console.log('dialog__overlay count:', await page.locator('.dialog__overlay').count());
    const distinct = [...new Set(errors.map((e) => e.split('\n')[0]))];
    console.log('distinct errors:\n  ' + (distinct.join('\n  ') || '(none)'));
  } catch (err) {
    console.log('--- DIAGNOSTICS ---');
    console.log('lane-7-archive present:', await archive.count());
    console.log('errors:\n  ' + (errors.join('\n  ') || '(none)'));
    await page.screenshot({ path: out('_debug-fullpage.png'), fullPage: true }).catch(() => {});
    throw err;
  } finally {
    await browser.close();
  }
}

main().catch((e) => { console.error('SHOT FAILED:', e.message); process.exit(1); });
