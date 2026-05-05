import { test, expect, Page } from '@playwright/test';

/**
 * ADR-0025 (three-stage review pipeline) regression spec.
 *
 * The board now renders seven lanes:
 *   1-preparation, 2-ready, 3-progress,
 *   4-auto-review, 5-human-review,
 *   6-completed, 7-archive.
 *
 * The two review lanes are explicit so the user can tell at a glance
 * which cards the orchestrator is still chewing on (auto-review, robot
 * icon) and which are waiting on them (human-review, eye icon). At the
 * canonical 1440 x 900 viewport every lane header must be visible
 * without horizontal overflow; the screenshot is the evidence that the
 * lane-layout-overflow guard from the paired task continues to hold.
 */

const FIXTURE_WATCH = 'C:/fixtures/seven-lanes-demo';
const FIXTURE_PROJECT = 'seven-lanes-demo';

function jobInfo(over: Partial<Record<string, unknown>>): Record<string, unknown> {
  const id = String(over['id'] ?? 'fx-job');
  const state = String(over['state'] ?? '2-ready');
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: String(over['title'] ?? id),
    state,
    order: Number(over['order'] ?? 1),
    agent: String(over['agent'] ?? 'claude'),
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-05T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: (over['cliType'] ?? 'claude') as string | null,
    useOwnSession: null,
    lastUsage: null,
    execution: over['execution'] ?? null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const autoReview = [
    jobInfo({ id: 'fx-auto-1', title: 'Orchestrator deciding', state: '4-auto-review' })
  ];
  const humanReview = [
    jobInfo({ id: 'fx-human-1', title: 'Awaiting your accept', state: '5-human-review' })
  ];
  return {
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting next thing', state: '1-preparation' })],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready to run', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-progress-1', title: 'Live run', state: '3-progress' })],
    autoReview,
    humanReview,
    review: autoReview, // legacy alias
    completed: [jobInfo({ id: 'fx-done-1', title: 'Wrapped up', state: '6-completed' })],
    archive: [jobInfo({ id: 'fx-arch-1', title: 'Old work', state: '7-archive' })]
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
  await page.route('**/api/clients/**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) });
  });
  await page.route('**/api/git/summary', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/cli/quota', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', ttlSeconds: 600, snapshots: [] }) });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', sections: [] }) });
  });
  await page.route('**/api/git/projects', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/orchestrator/global', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
  });
  await page.route('**/api/projects/*/settings', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ autoCommit: false, runnerMode: 'manual', orchestratorModel: null }) });
  });
  await page.route('**/api/dev-tools/flags', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }) });
  });
}

test.describe('ADR-0025 seven-lane kanban', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    // Pre-collapse the lanes that should default to "narrow" so the
    // seven full-width columns fit at 1440x900 without overflow. Without
    // this gesture the test would also pass when the lane-overflow guard
    // (paired task) is broken - we want the screenshot to reflect the
    // user's real default-collapses.
    await page.addInitScript(() => {
      window.localStorage.setItem(
        'collapsedLanes',
        JSON.stringify(['1-preparation', '7-archive'])
      );
    });
  });

  test('renders all seven lanes and stays within the 1440x900 viewport', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    // Every ADR-0025 lane must be visible somewhere on the board (either
    // expanded or as a rail).
    const lanes = [
      '1-preparation',
      '2-ready',
      '3-progress',
      '4-auto-review',
      '5-human-review',
      '6-completed',
      '7-archive',
    ];
    for (const state of lanes) {
      const expanded = page.getByTestId(`lane-collapse-${state}`);
      const rail = page.getByTestId(`lane-rail-${state}`);
      await expect.poll(async () =>
        (await expanded.count()) + (await rail.count()),
        { message: `lane ${state} should render either expanded or as a rail` }
      ).toBeGreaterThan(0);
    }

    // Auto Review and Human Review carry the distinct icons that
    // identify their audience (machine vs you). Pin to the column
    // heading so we don't also match the lowercase state-pill on each
    // job card.
    await expect(page.getByRole('heading', { name: 'Auto Review' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Human Review' })).toBeVisible();

    // Horizontal overflow guard: the kanban dashboard must fit inside
    // the 1440px viewport without producing a horizontal scroll bar.
    const overflow = await page.evaluate(() => {
      const el = document.querySelector('[data-testid="kanban-dashboard"]') as HTMLElement | null;
      if (!el) return { scroll: 0, client: 0 };
      return { scroll: el.scrollWidth, client: el.clientWidth };
    });
    expect(overflow.scroll).toBeLessThanOrEqual(overflow.client + 1);

    await page.screenshot({ path: 'test-results/kanban-seven-lanes-1440x900.png', fullPage: false });
  });
});
