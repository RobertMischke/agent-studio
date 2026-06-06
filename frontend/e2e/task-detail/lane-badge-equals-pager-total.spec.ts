import { test, expect, type Page } from '@playwright/test';

/**
 * Regression: the Review lane-count badge must equal the detail pager total
 * under an active project scope (the 116-vs-126 bug). The badge counts the
 * project-scoped `filteredGrouped` feed; the pager used to capture peers from
 * the RAW, unscoped grouped feed, so with a project filter active it iterated
 * every project's lane peers while the header showed only the scoped subset.
 *
 * Fully mocked — no backend. A Review (5-human-review) lane holds tasks from
 * two projects (alpha: 3, beta: 2). With the board scoped to alpha the badge
 * must read 3 and the pager that opens on an alpha task must read "x / 3".
 */

const ALPHA = 'fixture-lane-alpha';
const BETA = 'fixture-lane-beta';
const ALPHA_WP = 'C:/fixtures/lane-alpha';
const BETA_WP = 'C:/fixtures/lane-beta';

function makeTask(id: string, title: string, project: string, wp: string, order: number) {
  return {
    id,
    taskKey: `${wp}::${id}`,
    title,
    state: '5-human-review',
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-06-03T09:00:00Z',
    watchPath: wp,
    projectName: project,
    folderPath: `${wp}/.orchestrator/tasks/5-human-review/${id}`,
    lastActivity: '2026-06-03T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

const ALPHA_TASKS = [
  makeTask('alpha-1', 'Alpha review one', ALPHA, ALPHA_WP, 1),
  makeTask('alpha-2', 'Alpha review two', ALPHA, ALPHA_WP, 2),
  makeTask('alpha-3', 'Alpha review three', ALPHA, ALPHA_WP, 3),
];
const BETA_TASKS = [
  makeTask('beta-1', 'Beta review one', BETA, BETA_WP, 1),
  makeTask('beta-2', 'Beta review two', BETA, BETA_WP, 2),
];

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [],
  // Interleave projects so the lane spans both; the scoped badge must drop to 3.
  humanReview: [ALPHA_TASKS[0], BETA_TASKS[0], ALPHA_TASKS[1], BETA_TASKS[1], ALPHA_TASKS[2]],
  completed: [],
  archive: [],
};

function detailFor(task: ReturnType<typeof makeTask>) {
  return {
    info: task,
    promptMarkdown: '# ' + task.title,
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page) {
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));

  // Task-detail GET: `/api/tasks/<id>?watchPath=...`. Match before the bare
  // `/api/tasks` list route below.
  await page.route(/\/api\/tasks\/[^/?]+(\?|$)/, (route) => {
    const url = new URL(route.request().url());
    const id = decodeURIComponent(url.pathname.split('/').pop() ?? '');
    const all = [...ALPHA_TASKS, ...BETA_TASKS];
    const task = all.find(t => t.id === id);
    if (task) {
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detailFor(task)) });
      return;
    }
    route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
  });

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: ALPHA, path: ALPHA_WP, rootPath: ALPHA_WP, repositoryPath: ALPHA_WP },
        { name: BETA, path: BETA_WP, rootPath: BETA_WP, repositoryPath: BETA_WP },
      ]),
    }));

  await page.route('**/api/runner/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [ALPHA]: { projectName: ALPHA, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
          [BETA]: { projectName: BETA, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));

  // Benign empties for everything else the shell pings on boot.
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-03T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-03T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));

  // Catch-all for any other /api call so nothing 404s into an error modal.
  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });
}

async function gotoScopedBoard(page: Page): Promise<void> {
  // Seed an "all projects" board tab + an ACTIVE alpha-only project filter so
  // the Review badge is project-scoped from first paint.
  await page.addInitScript(({ alpha }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    localStorage.setItem('activeProjects', JSON.stringify([alpha]));
  }, { alpha: ALPHA });

  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="lane-5-human-review"]').first()).toBeVisible({ timeout: 15_000 });
}

test.describe('Lane badge == pager total (project-scoped Review)', () => {
  test('Review badge and pager both read 3 under the alpha filter', async ({ page }, testInfo) => {
    await gotoScopedBoard(page);

    // Badge: scoped to alpha → 3 (not the raw 5 across both projects).
    const badge = page.locator('[data-testid="lane-5-human-review"] .column__count');
    await expect(badge).toHaveText('3');

    // Open an alpha Review task → pager captures the SCOPED peers.
    await page.locator('[data-testid="lane-5-human-review"] app-job-card', { hasText: 'Alpha review one' })
      .first().click();

    const pager = page.getByTestId('lane-pager-count');
    await expect(pager).toBeVisible({ timeout: 10_000 });
    // Single source of truth: pager total mirrors the badge.
    await expect(pager).toHaveText(/\/\s*3$/);

    const badgeText = (await badge.textContent())?.trim();
    const pagerText = (await pager.textContent())?.trim();
    const pagerTotal = pagerText?.split('/').pop()?.trim();
    expect(pagerTotal).toBe(badgeText);

    // Visual evidence for review.
    await page.setViewportSize({ width: 1600, height: 1100 });
    await page.evaluate(() => {
      document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
    });
    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      await page.screenshot({ path: `${resultsDir}/lane-badge-equals-pager-total.png`, fullPage: false });
    }
    await page.screenshot({ path: 'test-results/lane-badge-equals-pager-total.png', fullPage: false });
    const buf = await page.screenshot({ fullPage: false });
    await testInfo.attach('lane-badge-equals-pager-total.png', { body: buf, contentType: 'image/png' });
  });
});
