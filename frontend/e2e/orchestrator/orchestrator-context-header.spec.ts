import { test, expect, Page } from '@playwright/test';

/**
 * Regression coverage for the orchestrator "where am I right now" header
 * (`<app-orchestrator-context-header>`) wired into the side sheet.
 *
 * The chat + task endpoints are stubbed so the spec runs without a live
 * backend or CLI quota. What we lock:
 *   1. On board scope the header shows the active project chip and the
 *      "Board" scope chip (no task, no lane).
 *   2. When a CLI run is live in the active project, the header surfaces
 *      the live-run pill with the short model name and a ticking duration
 *      even without opening the task detail (board-scope run resolution
 *      via `App.orchSideSheetActiveRun`).
 *
 * The task-scope rendering (task key + title + lane pill) and the elapsed
 * formatter are covered exhaustively by the component unit spec; this E2E
 * proves the real-app wiring and produces the review screenshot.
 */

const PROJECT = 'project-neuen';
const RUNNING_TASK_ID = 'run-task-1';
const RUNNING_TASK_TITLE = 'Wire up the orchestrator header';

function runningTask() {
  return {
    id: RUNNING_TASK_ID,
    taskKey: PROJECT + '::' + RUNNING_TASK_ID,
    displayKey: 'AGT-1916',
    title: RUNNING_TASK_TITLE,
    state: '3-progress',
    order: 0,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    createdAt: new Date().toISOString(),
    watchPath: 'C:/tmp/' + PROJECT,
    projectName: PROJECT,
    folderPath: 'C:/tmp/' + PROJECT + '/' + RUNNING_TASK_ID,
    lastActivity: new Date().toISOString(),
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    commit: null,
    execution: {
      jobId: RUNNING_TASK_ID,
      taskKey: PROJECT + '::' + RUNNING_TASK_ID,
      processId: 1234,
      // Two minutes ago -> duration label bucket "2m".
      startedAt: new Date(Date.now() - 120_000).toISOString(),
      status: 'running',
      exitCode: null,
      durationSeconds: null,
      model: 'claude-opus-4-8',
      thinkingLevel: null,
      runOutcome: null,
    },
  };
}

/**
 * Board bootstrap fires ~14 read polls beyond the four this spec cares about
 * (CLI models, quota, runner status, workspaces, project settings, tags,
 * sessions, archive, …). With no live backend every one 502s through the dev
 * proxy; the global `ModalErrorHandler` then pops a blocking error dialog whose
 * overlay intercepts our clicks. Stub the remainder with benign empty payloads
 * so the app boots hermetically without a backend. Registered first so the four
 * focused routes below — matched last-registered-first by Playwright — win.
 */
async function stubBoardBootstrap(page: Page) {
  await page.route(/\/api\//, async (route) => {
    if (route.request().method() !== 'GET') { await route.continue(); return; }
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  // SignalR jobs hub: reject the negotiate so the client fails fast instead of
  // spamming retries. The offline banner it raises is a fixed top strip that is
  // cosmetic and non-blocking for the status-bar controls this spec drives.
  await page.route(/\/hubs\/jobs\/negotiate/, async (route) => {
    await route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
  });
}

async function stubWorkspace(page: Page, opts: { withRunningTask: boolean }) {
  await stubBoardBootstrap(page);

  await page.route(/\/api\/watch-paths$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: 'C:/tmp/' + PROJECT, rootPath: 'C:/tmp/' + PROJECT, repositoryPath: '' },
      ]),
    });
  });

  const flatTasks = opts.withRunningTask ? [runningTask()] : [];
  await page.route(/\/api\/tasks(?:\?.*)?$/, async (route) => {
    if (route.request().method() !== 'GET') { await route.continue(); return; }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(flatTasks),
    });
  });

  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, async (route) => {
    const empty = {
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [],
    };
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(opts.withRunningTask ? { ...empty, progress: [runningTask()] } : empty),
    });
  });

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(route.request().url());
    const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project, turns: [] }),
    });
  });
}

/**
 * The dev backend is offline in this run, so background polls for endpoints
 * we do not stub fail and pop the shared error dialog. Its overlay would
 * intercept our clicks; dismiss any that are open before proceeding. Polls
 * run on long intervals so, once cleared, they do not recur within the test.
 */
async function dismissErrorDialogs(page: Page) {
  for (let i = 0; i < 5; i++) {
    const overlay = page.getByTestId('error-dialog-overlay');
    if ((await overlay.count()) === 0) return;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
  }
}

async function openSideSheet(page: Page) {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await dismissErrorDialogs(page);
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
}

test.describe('Orchestrator context header · where am I', () => {
  test('board scope shows the project chip and the Board scope chip', async ({ page }) => {
    await stubWorkspace(page, { withRunningTask: false });
    await openSideSheet(page);

    const header = page.getByTestId('orch-context-header');
    await expect(header).toBeVisible();
    await expect(header).toHaveAttribute('data-scope', 'board');
    await expect(page.getByTestId('orch-context-project')).toContainText(PROJECT);
    await expect(page.getByTestId('orch-context-board')).toHaveText('Board');
    // Nothing running -> no live-run pill.
    await expect(page.getByTestId('orch-context-run')).toHaveCount(0);
  });

  test('surfaces the live run (model + duration) when a run is active in the project', async ({ page }) => {
    await stubWorkspace(page, { withRunningTask: true });
    await openSideSheet(page);

    const header = page.getByTestId('orch-context-header');
    await expect(header).toBeVisible();
    await expect(page.getByTestId('orch-context-project')).toContainText(PROJECT);

    const run = page.getByTestId('orch-context-run');
    await expect(run).toBeVisible();
    await expect(page.getByTestId('orch-context-run-model')).toHaveText('opus 4.8');
    await expect(page.getByTestId('orch-context-run-duration')).toBeVisible();

    await page.screenshot({
      path: 'screenshots/orchestrator-context-header/live-run--mocked.png',
      fullPage: false,
    });
  });
});
