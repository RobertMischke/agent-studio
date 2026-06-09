import { test, expect, Page } from '@playwright/test';

/**
 * Regression guard for the lane-rename task: the board used to surface the
 * 2-ready and 5-human-review lanes as "Human Ready" and "Human Review".
 * The user dropped the human/non-human distinction entirely - those lanes
 * now read simply "Ready" and "Review". The orchestrator-owned pass
 * (4-auto-review) now reads "Post Processing".
 *
 * The underlying state keys (2-ready, 5-human-review, 4-auto-review) are
 * unchanged - this is a display-label change only - so the mock fixture
 * still groups jobs under the same state keys; only the rendered headings
 * differ.
 *
 * This spec mocks every API call, so it renders the board from the lane
 * config alone and needs no live backend.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-rename-demo';
const FIXTURE_PROJECT = 'lane-rename-demo';

function jobInfo(id: string, state: string, title: string): Record<string, unknown> {
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title,
    state,
    order: 1,
    agent: 'claude',
    createdAt: '2026-06-02T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-06-02T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null,
    tags: [],
    taskType: 'chore'
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const autoReview = [jobInfo('fx-auto-1', '4-auto-review', 'Orchestrator deciding')];
  const humanReview = [jobInfo('fx-human-1', '5-human-review', 'Awaiting your accept')];
  return {
    preparation: [jobInfo('fx-prep-1', '1-preparation', 'Drafting next thing')],
    ready: [jobInfo('fx-ready-1', '2-ready', 'Ready to run')],
    progress: [jobInfo('fx-progress-1', '3-progress', 'Live run')],
    autoReview,
    humanReview,
    review: autoReview, // legacy alias
    completed: [jobInfo('fx-done-1', '6-completed', 'Wrapped up')],
    archive: [jobInfo('fx-arch-1', '7-archive', 'Old work')]
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = [
    ...grouped.preparation, ...grouped.ready, ...grouped.progress,
    ...grouped.autoReview, ...grouped.humanReview,
    ...grouped.completed, ...grouped.archive
  ];

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]) });
  });
  await page.route('**/api/jobs', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/jobs/grouped', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/runner/status', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [FIXTURE_PROJECT]: { projectName: FIXTURE_PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }) });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) });
  });
  await page.route('**/api/projects/settings', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
  });
}

test.describe('lane rename - no "Human" prefix', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
  });

  test('renders Ready / Review / Post Processing headings and never legacy human or auto-review headings', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
      .toBeVisible({ timeout: 10_000 });

    // The renamed lanes.
    await expect(page.getByRole('heading', { name: 'Review', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Ready', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Post Processing' })).toBeVisible();

    // The dropped labels must be gone from every heading on the board.
    await expect(page.getByRole('heading', { name: /Human Review/ })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: /Human Ready/ })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: /Auto Review/ })).toHaveCount(0);

    await page.screenshot({ path: 'test-results/lane-rename-no-human-prefix-1440x900.png', fullPage: false });
  });
});
