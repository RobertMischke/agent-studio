// Throwaway visual-evidence capture for ASS-1722 (lane rename + button rename).
// Fully mocked (no backend): drives the worktree dev server and screenshots
//   1) the board showing the "Delivered" lane label on a 6-completed card
//   2) the task-detail header showing the "Merge into Develop" primary button
// Writes PNGs straight into the job folder's results/ (the evidence gate's dir).
import { chromium } from '@playwright/test';
import fs from 'node:fs';

const BASE = process.env.PW_BASE_URL || 'http://127.0.0.1:4012';
const OUT = process.env.OUT_DIR;
if (!OUT) throw new Error('OUT_DIR (job results dir) must be set');
fs.mkdirSync(OUT, { recursive: true });

const PROJECT = 'fixture-pill-lane';
const WATCH_PATH = 'C:/fixtures/pill-lane-repo';

function makeTask(id, state, title, order) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, title, state, order,
    agent: 'claude', cliType: 'claude', createdAt: '2026-05-28T09:00:00Z',
    watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-05-28T11:00:00Z', sessionName: null,
    model: 'claude-opus-4-7', useOwnSession: null, lastUsage: null,
    execution: null, commit: null, commits: [], ownerClientId: 'local-default', tags: [],
  };
}

const HUMAN_REVIEW_TASK = makeTask('pill-lane-C-human', '5-human-review', 'Pill lane human review charlie', 1);
const COMPLETED_TASK = makeTask('pill-lane-D-done', '6-completed', 'Pill lane completed delta', 1);

const GROUPED_PAYLOAD = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], review: [], autoReview: [],
  humanReview: [HUMAN_REVIEW_TASK], completed: [COMPLETED_TASK], archive: [],
};

const ALL = [HUMAN_REVIEW_TASK, COMPLETED_TASK];

async function installRoutes(page) {
  // Playwright matches the MOST-RECENTLY-registered route first, so register
  // the broad catch-all FIRST and the specific overrides AFTER it.
  // Catch-all: every other /api/** sub-resource returns an empty 200 so no
  // 404 error-modal overlay can intercept clicks.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  // Single-task detail: return a full TaskDetail envelope (info + empty
  // sub-collections) so task-selection.service.openDetail() -> getDetail()
  // resolves and the triage panel (gated on selectedJob()) can render.
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
  // Grouped board payload (registered after single-task so it wins for /grouped).
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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-28T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-28T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
}

async function dismissOverlays(page) {
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
    document.querySelectorAll('.overlay--error').forEach((n) => ((n).style.display = 'none'));
    // App-level error dialog raised by a failing sub-resource fetch in the
    // backendless mock; remove it + its backdrop so it doesn't cover the header.
    document.querySelectorAll('app-error-dialog, .cdk-overlay-container, .cdk-overlay-backdrop, .modal-backdrop')
      .forEach((n) => n.remove());
  });
}

const run = async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('console', (m) => { if (m.type() === 'error') console.log('  [browser err]', m.text().slice(0, 160)); });

  await page.addInitScript(() => {
    try {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__' }));
      document.documentElement.dataset['studioTheme'] = 'dark';
      localStorage.setItem('atp.studio.theme', 'dark');
    } catch {}
  });

  await installRoutes(page);

  console.log('Navigate board', BASE);
  await page.goto(BASE + '/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first().waitFor({ state: 'visible', timeout: 20000 });
  await page.locator('[data-testid="task-card"]').first().waitFor({ state: 'visible', timeout: 20000 });
  await dismissOverlays(page);
  await page.waitForTimeout(300);

  // ---- Evidence 1: the "Delivered" lane label on the 6-completed card ----
  const doneCard = page.locator('[data-testid="task-card"]', { hasText: COMPLETED_TASK.title });
  const donePill = doneCard.locator('.task-card__state-pill');
  const pillText = (await donePill.textContent())?.trim();
  console.log('  completed card state-pill =', JSON.stringify(pillText));
  if (pillText !== 'Delivered') throw new Error(`ASSERT FAILED: completed card pill expected "Delivered", got "${pillText}"`);
  await page.screenshot({ path: `${OUT}/01-board-delivered-lane.png`, fullPage: false });
  console.log('  ok: wrote 01-board-delivered-lane.png');

  // ---- Evidence 2: the "Merge into Develop" primary button in detail header ----
  // Open the detail by clicking the human-review card already loaded on the
  // board, so the task object (state 5-human-review) comes from the in-memory
  // grouped payload rather than a re-fetched/assembled single-task projection.
  const reviewCard = page.locator('[data-testid="task-card"]', { hasText: HUMAN_REVIEW_TASK.title });
  await reviewCard.first().click();
  await dismissOverlays(page);
  const panel = page.getByTestId('studio-triage-panel');
  await panel.waitFor({ state: 'visible', timeout: 15000 });
  const primary = page.getByTestId('studio-triage-action-mark-done');
  await primary.waitFor({ state: 'visible', timeout: 15000 });
  const btnText = (await primary.textContent())?.trim();
  console.log('  primary action button =', JSON.stringify(btnText));
  if (!btnText || !btnText.includes('Merge into Develop')) {
    throw new Error(`ASSERT FAILED: primary button expected "Merge into Develop", got "${btnText}"`);
  }
  await dismissOverlays(page);
  await page.waitForTimeout(200);
  await page.screenshot({ path: `${OUT}/02-merge-into-develop-button.png`, fullPage: false });
  console.log('  ok: wrote 02-merge-into-develop-button.png');

  await browser.close();
  console.log('DONE');
};

run().catch((e) => { console.error(e); process.exit(1); });
