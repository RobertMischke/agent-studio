// ASS-1751 in-context visual evidence for the 3-progress run-activity pill.
// Backend-free: drives the REAL full app dev server with all /api/** mocked, so
// the screenshots show the production task-card + studio detail header pills.
//   1) the board: all four progress cards, each with its distinct German pill
//      (Run aktiv / failed · Backoff bis HH:MM / failed · kein aktiver Run /
//       kein aktiver Run)
//   2) the studio detail header: the failed-backoff card's "Backoff bis HH:MM" pill
// Writes PNGs straight into the job folder's results/ (the evidence gate's dir).
//
// The badge is built entirely on the frontend by buildRunActivityBadge() from
// job.runActivity + state===Progress, so mocked TaskInfo payloads exercise the
// real component + real SCSS + real util — a faithful render of production.
import { chromium } from '@playwright/test';
import fs from 'node:fs';

const BASE = process.env.PW_BASE_URL || 'http://127.0.0.1:4012';
const OUT = process.env.OUT_DIR
  || 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/tasks/001/ASS-1751/results';
fs.mkdirSync(OUT, { recursive: true });

const PROJECT = 'fixture-run-activity';
const WATCH_PATH = 'C:/fixtures/run-activity-repo';

// +90s from "now" so the failed-backoff pill shows a future retry clock.
const BACKOFF_UNTIL = new Date(Date.now() + 90_000).toISOString();

function makeTask(id, title, order, runActivity) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, key: `ASS-${order + 1750}`, title,
    state: '3-progress', order,
    agent: 'codex', cliType: 'codex', createdAt: '2026-06-10T09:00:00Z',
    watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/3-progress/${id}`,
    lastActivity: '2026-06-10T09:30:00Z', sessionName: null,
    model: 'gpt-5-codex', useOwnSession: null, lastUsage: null,
    execution: null, commit: null, commits: [], ownerClientId: 'local-default', tags: [],
    runActivity,
  };
}

const ACTIVE = makeTask('ra-active', 'Run-activity active alpha', 1,
  { kind: 'active', processId: 4242, attempt: 0 });
const BACKOFF = makeTask('ra-backoff', 'Run-activity failed backoff bravo', 2,
  { kind: 'failed-backoff', backoffUntil: BACKOFF_UNTIL, attempt: 2, lastError: 'git push rejected (non-fast-forward)' });
const FAILED_IDLE = makeTask('ra-failed-idle', 'Run-activity failed idle charlie', 3,
  { kind: 'failed-idle', attempt: 1, lastError: 'run ended without sentinel' });
const ORPHAN = makeTask('ra-orphan', 'Run-activity orphan delta', 4,
  { kind: 'no-active-run', attempt: 0 });

const PROGRESS = [ACTIVE, BACKOFF, FAILED_IDLE, ORPHAN];
const ALL = [...PROGRESS];

const GROUPED_PAYLOAD = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: PROGRESS,
  failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function installRoutes(page) {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route(/\/api\/tasks\/([^/?]+)(\?|$)/, (route) => {
    const m = route.request().url().match(/\/api\/tasks\/([^/?]+)(?:\?|$)/);
    const id = m ? decodeURIComponent(m[1]) : '';
    const hit = ALL.find((t) => t.id === id);
    const detail = hit ? {
      info: hit, promptMarkdown: null, promptHistory: [], titleHistory: [],
      statusMarkdown: null, statusGeneration: null, contextUsage: null,
      log: [], summaryState: null, reviewEvidence: [],
    } : {};
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) });
  });
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]) }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }) }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-10T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-10T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/tasks\/[^/?]+\/session-events(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
}

async function dismissOverlays(page) {
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
    document.querySelectorAll('.overlay--error').forEach((n) => ((n).style.display = 'none'));
    document.querySelectorAll('app-error-dialog, .cdk-overlay-container, .cdk-overlay-backdrop, .modal-backdrop')
      .forEach((n) => n.remove());
  });
}

const run = async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 1100 } });
  page.on('console', (m) => { if (m.type() === 'error') console.log('  [browser err]', m.text().slice(0, 160)); });

  await page.addInitScript(() => {
    try {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__' }));
      document.documentElement.dataset['studioTheme'] = 'dark';
      localStorage.setItem('atp.studio.theme', 'dark');
      const inject = () => {
        const s = document.createElement('style');
        s.textContent = 'app-error-dialog, .cdk-overlay-container, .cdk-overlay-backdrop, .modal-backdrop, vite-error-overlay { display: none !important; }';
        document.head.appendChild(s);
      };
      if (document.head) inject();
      else document.addEventListener('DOMContentLoaded', inject, { once: true });
    } catch {}
  });

  await installRoutes(page);

  console.log('Navigate board', BASE);
  await page.goto(BASE + '/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first().waitFor({ state: 'visible', timeout: 30000 });
  await page.locator('[data-testid="task-card"]').first().waitFor({ state: 'visible', timeout: 30000 });
  await dismissOverlays(page);
  await page.waitForTimeout(400);

  // ---- Assert each card's pill kind + German label ----
  const expected = {
    [ACTIVE.title]: { kind: 'active', label: 'Run aktiv' },
    [BACKOFF.title]: { kind: 'failed-backoff', labelRe: /^failed · Backoff bis \d{2}:\d{2}$/ },
    [FAILED_IDLE.title]: { kind: 'failed-idle', label: 'failed · kein aktiver Run' },
    [ORPHAN.title]: { kind: 'no-active-run', label: 'kein aktiver Run' },
  };
  for (const [title, exp] of Object.entries(expected)) {
    const card = page.locator('[data-testid="task-card"]', { hasText: title });
    const pill = card.locator('[data-testid="task-card-run-activity"]');
    await pill.first().waitFor({ state: 'visible', timeout: 10000 });
    const kind = await pill.first().getAttribute('data-run-activity-kind');
    const label = (await pill.first().textContent())?.trim();
    console.log(`  card "${title}" -> kind=${kind} label=${JSON.stringify(label)}`);
    if (kind !== exp.kind) throw new Error(`ASSERT FAILED: ${title} kind expected ${exp.kind}, got ${kind}`);
    if (exp.label && label !== exp.label) throw new Error(`ASSERT FAILED: ${title} label expected ${exp.label}, got ${label}`);
    if (exp.labelRe && !exp.labelRe.test(label || '')) throw new Error(`ASSERT FAILED: ${title} label ${JSON.stringify(label)} !~ ${exp.labelRe}`);
  }

  await dismissOverlays(page);
  await page.screenshot({ path: `${OUT}/01-board-progress-run-activity-pills.png`, fullPage: false });
  console.log('  ok: wrote 01-board-progress-run-activity-pills.png');

  // ---- Studio detail header: open the failed-backoff card, screenshot the
  // run-activity pill in the slim tab-bar header (studio shell hides
  // <app-detail-header>, so the visible header pill is studio-run-activity). ----
  const backoffCard = page.locator('[data-testid="task-card"]', { hasText: BACKOFF.title });
  await backoffCard.first().click();
  for (let i = 0; i < 6; i++) { await dismissOverlays(page); await page.waitForTimeout(250); }
  const headerPill = page.locator('[data-testid="studio-run-activity"]');
  await headerPill.first().waitFor({ state: 'visible', timeout: 15000 });
  const hKind = await headerPill.first().getAttribute('data-run-activity-kind');
  const hLabel = (await headerPill.first().textContent())?.trim();
  console.log(`  studio header pill -> kind=${hKind} label=${JSON.stringify(hLabel)}`);

  if (hKind !== 'failed-backoff') throw new Error(`ASSERT FAILED: studio header kind expected failed-backoff, got ${hKind}`);
  if (!/^failed · Backoff bis \d{2}:\d{2}$/.test(hLabel || '')) throw new Error(`ASSERT FAILED: studio header label ${JSON.stringify(hLabel)} bad`);

  await dismissOverlays(page);
  await page.waitForTimeout(200);
  await page.screenshot({ path: `${OUT}/02-detail-header-failed-backoff.png`, clip: { x: 0, y: 0, width: 1600, height: 78 } });
  console.log('  ok: wrote 02-detail-header-failed-backoff.png');

  const tabActions = page.locator('[data-testid="studio-tab-actions"]').first();
  if (await tabActions.isVisible().catch(() => false)) {
    await tabActions.screenshot({ path: `${OUT}/03-studio-header-pill-crop.png` }).catch(() => undefined);
    console.log('  ok: wrote 03-studio-header-pill-crop.png');
  }

  await browser.close();
  console.log('DONE');
};

run().catch((e) => { console.error(e); process.exit(1); });
