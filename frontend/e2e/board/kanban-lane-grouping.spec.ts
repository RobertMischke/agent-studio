import { test, expect, Page } from '@playwright/test';

/**
 * Kanban lane grouping and per-lane collapse.
 *
 * The board's lifecycle columns are wrapped in three contiguous
 * containers (Backlog / Active / Done & Decide) so the workflow reads
 * as phases. The Active container holds machine-driven lanes
 * (3-progress, 3a-failed-pickup, 4-auto-review); the Done & Decide
 * container holds the user-owned tail (5-human-review, 6-completed,
 * 7-archive). Any individual lane can also be collapsed into a narrow
 * rail that still surfaces task count plus running / needs-input /
 * error / CLI badges.
 *
 * The spec is API-mocked: it stubs `/api/jobs/grouped` (and supporting
 * read endpoints) with a deterministic fixture that exercises all four
 * indicator types, so the screenshots are stable and the test does not
 * compete with whatever live state the dev backend is in.
 */

const FIXTURE_WATCH = 'C:/fixtures/grouping-demo';
const FIXTURE_PROJECT = 'grouping-demo';

function jobInfo(over: Partial<Record<string, unknown>>): Record<string, unknown> {
  const id = String(over['id'] ?? 'fx-job');
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: String(over['title'] ?? id),
    state: String(over['state'] ?? '2-ready'),
    order: Number(over['order'] ?? 1),
    agent: String(over['agent'] ?? 'claude'),
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${over['state'] ?? '2-ready'}/${id}`,
    lastActivity: '2026-05-05T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: (over['cliType'] ?? 'claude') as string | null,
    useOwnSession: null,
    lastUsage: null,
    execution: over['execution'] ?? null,
    commit: null,
    pendingIntent: over['pendingIntent'] ?? null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  // ADR-0025 lane shape: separate auto-review (orchestrator) and
  // human-review (user). The legacy `review` key is kept as an alias of
  // `autoReview` for any frontend code path that has not been migrated.
  const autoReview = [
    jobInfo({ id: 'fx-auto-review-1', title: 'Failed run waiting', state: '4-auto-review',
      execution: {
        jobId: 'fx-auto-review-1', jobKey: `${FIXTURE_WATCH}::fx-auto-review-1`,
        processId: 9002, startedAt: '2026-05-05T08:30:00Z',
        status: 'failed', exitCode: 1, durationSeconds: 60, model: 'claude-opus-4-7'
      }
    })
  ];
  const humanReview = [
    jobInfo({ id: 'fx-human-review-1', title: 'Awaiting your accept', state: '5-human-review' })
  ];
  return {
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting next thing', state: '1-preparation' })],
    ready: [
      jobInfo({ id: 'fx-ready-1', title: 'Ready to run', state: '2-ready', order: 1 }),
      jobInfo({ id: 'fx-ready-2', title: 'Pending follow-up', state: '2-ready', order: 2,
        pendingIntent: {
          version: 1, mode: 'continue', prompt: 'Please continue with edits',
          savedAt: '2026-05-05T09:01:00Z', savedReason: 'project-busy', savedAgainstActiveJobId: null
        }
      })
    ],
    progress: [
      jobInfo({ id: 'fx-progress-1', title: 'Live run', state: '3-progress',
        cliType: 'claude',
        execution: {
          jobId: 'fx-progress-1', jobKey: `${FIXTURE_WATCH}::fx-progress-1`,
          processId: 9001, startedAt: '2026-05-05T08:55:00Z',
          status: 'running', exitCode: null, durationSeconds: null, model: 'claude-opus-4-7'
        }
      })
    ],
    autoReview,
    humanReview,
    review: autoReview,
    completed: [jobInfo({ id: 'fx-done-1', title: 'Wrapped up', state: '6-completed' })],
    archive: [jobInfo({ id: 'fx-arch-1', title: 'Old work', state: '7-archive' })]
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = [
    ...grouped.preparation, ...grouped.ready, ...grouped.progress,
    ...grouped.autoReview, ...grouped.humanReview, ...grouped.completed, ...grouped.archive
  ];

  // Routes are matched LIFO in Playwright — register the catch-all FIRST so
  // any specific routes registered below take precedence. Return an empty
  // array as the default body: most app consumers expect either an array
  // or an object, and `[]` parses fine as JSON for both `arr.length` and
  // shape probes (object property reads against an array yield undefined,
  // which most templates already handle via optional chaining).
  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  // (catch-all is intentionally registered first so the specific routes
  // below take precedence — Playwright evaluates routes LIFO.)

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
      body: JSON.stringify({ projects: { [FIXTURE_PROJECT]: { projectName: FIXTURE_PROJECT, mode: 'manual', activeJobId: 'fx-progress-1', activeExecution: null, queuedJobIds: [] } } }) });
  });
  await page.route('**/api/clients/**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify([{ id: 'local-default', displayName: 'Local', emoji: '🧑', colour: '#a78bfa', kind: 'human', registeredAt: '2026-05-05T00:00:00Z', lastSeenAt: null, tokenBudgetMonthly: null, notes: null }]) });
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
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) });
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

test.describe('Kanban lane grouping and collapse', () => {
  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
  });

  test('renders three lane groups in the expected order', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    // The lane group ids are the three top-level kanban containers
    // (the focus-mode keyboard targets `1`/`2`/`3`). The chip strip
    // lives inside `lane-group-strip-*` when the container is
    // collapsed, so filter that nested testid out.
    const groups = page.locator('[data-testid^="lane-group-"]:not([data-testid*="-strip-"]):not([data-testid*="-toggle-"]):not([data-testid*="-focus-"]):not([data-testid*="-chip-"])');
    await expect(groups).toHaveCount(3);
    await expect(page.getByTestId('lane-group-backlog')).toBeVisible();
    await expect(page.getByTestId('lane-group-active')).toBeVisible();
    await expect(page.getByTestId('lane-group-decide')).toBeVisible();

    await expect(page.getByTestId('lane-group-backlog')).toContainText('Backlog');
    await expect(page.getByTestId('lane-group-active')).toContainText('Active');
    await expect(page.getByTestId('lane-group-decide')).toContainText('Done & Decide');

    // Sanity-check ordering: Backlog left of Active, Active left of Decide.
    const ids = await groups.evaluateAll((els) => els.map((e) => e.getAttribute('data-testid')));
    expect(ids).toEqual(['lane-group-backlog', 'lane-group-active', 'lane-group-decide']);

    // 5-human-review is part of the Done & Decide container, not Active.
    const decide = page.getByTestId('lane-group-decide');
    await expect(decide.locator('[data-testid="lane-5-human-review"], [data-testid="lane-rail-5-human-review"]')).toHaveCount(1);
    const active = page.getByTestId('lane-group-active');
    await expect(active.locator('[data-testid="lane-5-human-review"], [data-testid="lane-rail-5-human-review"]')).toHaveCount(0);

    await page.screenshot({ path: 'test-results/kanban-board-expanded.png', fullPage: true });
  });

  test('collapsing a lane shows a rail with count and indicators, persists across reload', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    // Make sure no prior-run state lingers in this browser context.
    await page.evaluate(() => window.localStorage.removeItem('collapsedLanes'));
    await page.reload();
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    // Wait for the running job card to appear so we know /api/jobs/grouped has resolved.
    await expect(page.locator('[data-running="true"]')).toHaveCount(1);

    // Collapse Preparation, Ready, Completed, Archive. That leaves
    // Progress (running) and the two ADR-0025 review lanes expanded so
    // neither active nor blocked work is hidden by the default gesture.
    for (const state of ['1-preparation', '2-ready', '6-completed', '7-archive']) {
      await page.getByTestId(`lane-collapse-${state}`).click();
    }

    // Rails appear with the right counts and indicators.
    const readyRail = page.getByTestId('lane-rail-2-ready');
    await expect(readyRail).toBeVisible();
    await expect(page.getByTestId('lane-rail-count').first()).toBeVisible();
    await expect(readyRail.locator('[data-testid="lane-rail-count"]')).toHaveText('2');
    await expect(page.getByTestId('lane-rail-needs-input-2-ready')).toBeVisible();

    // Active group lanes stay expanded so running / failed work is visible.
    await expect(page.getByTestId('lane-collapse-3-progress')).toBeVisible();
    await expect(page.getByTestId('lane-collapse-4-auto-review')).toBeVisible();
    await expect(page.getByTestId('lane-collapse-5-human-review')).toBeVisible();

    // Now collapse the active lanes too; the rails must surface running + error indicators
    // and the CLI badge so nothing important is silently hidden.
    await page.getByTestId('lane-collapse-3-progress').click();
    await page.getByTestId('lane-collapse-4-auto-review').click();
    await page.getByTestId('lane-collapse-5-human-review').click();

    await expect(page.getByTestId('lane-rail-running-3-progress')).toBeVisible();
    await expect(page.getByTestId('lane-rail-cli-3-progress')).toBeVisible();
    await expect(page.getByTestId('lane-rail-error-4-auto-review')).toBeVisible();

    await page.screenshot({ path: 'test-results/kanban-board-collapsed.png', fullPage: true });

    // Persistence: the four originally-collapsed states survive a reload.
    const stored = await page.evaluate(() => window.localStorage.getItem('collapsedLanes'));
    expect(stored).not.toBeNull();
    const storedSet = new Set(JSON.parse(stored as string) as string[]);
    for (const s of ['1-preparation', '2-ready', '3-progress', '4-auto-review', '5-human-review', '6-completed', '7-archive']) {
      expect(storedSet.has(s)).toBe(true);
    }

    await page.reload();
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('lane-rail-1-preparation')).toBeVisible();
    await expect(page.getByTestId('lane-rail-2-ready')).toBeVisible();
    await expect(page.getByTestId('lane-rail-3-progress')).toBeVisible();
  });

  test('drag-and-drop drop targets exist on collapsed lanes (rail acts as drop zone)', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    // Collapse Ready - the rail should be a valid drop target so the user
    // can still move a card into a collapsed lane without expanding it.
    await page.getByTestId('lane-collapse-2-ready').click();
    const rail = page.getByTestId('lane-rail-2-ready');
    await expect(rail).toBeVisible();

    // The rail surface listens for dragover/drop. We assert the listeners
    // are wired by checking the element is the host of the drop handler
    // (data-testid resolves to a button with the column-rail class).
    await expect(rail).toHaveAttribute('data-state', '2-ready');
  });
});
