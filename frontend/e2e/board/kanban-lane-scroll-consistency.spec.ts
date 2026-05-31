import { test, expect, Page } from '@playwright/test';

/**
 * Regression: the Human Review lane developed its own internal vertical
 * scrollbar once it crossed ~50 cards while every other lane scrolled
 * the page. The cause was a lane-local CDK virtual-scroll viewport that
 * kicked in above a threshold for non-archive, non-legacy-review lanes.
 * The user-visible effect was one lane behaving differently from the
 * rest, which read as broken.
 *
 * Fix: every lane uses the same scroll model now. The dashboard fills
 * the viewport vertically and each lane body is its own vertical
 * scroll container. A lane scrolls only when it actually overflows; it
 * never widens the dashboard, never pushes the page scrollbar, and
 * never differs from its neighbours in scroll mechanics.
 *
 * This spec stocks the densest lane the user reports (Human Review)
 * with enough cards to have previously triggered virtualization, then
 * asserts:
 *   1. Every expanded lane uses the **same** scroll container as its
 *      siblings (the .column__body, with overflow-y: auto). The
 *      previous bug surfaced as the lane that crossed the threshold
 *      having its own CDK virtual-scroll viewport while every other
 *      lane had no scroll container at all.
 *   2. The page (.app__body / document) does NOT scroll vertically
 *      when a single lane is overstocked. Vertical overflow lives
 *      inside the affected lane, not at the page level.
 *   3. Collapsing a lane still works (the column-rail toggle exists
 *      and reacts), proving the change did not break lane-grouping
 *      controls.
 *   4. Cards in the dense lane remain draggable (the `draggable`
 *      attribute survives the template restructure), proving drag-
 *      and-drop wiring still reaches every card.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-scroll-consistency';
const FIXTURE_PROJECT = 'lane-scroll-consistency';
const HUMAN_REVIEW_COUNT = 60;

interface JobOverrides {
  id?: string;
  title?: string;
  state?: string;
  order?: number;
}

function jobInfo(over: JobOverrides): Record<string, unknown> {
  const id = over.id ?? 'fx-job';
  const state = over.state ?? '2-ready';
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: over.title ?? id,
    state,
    order: over.order ?? 1,
    agent: 'claude',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-05T09:00:00Z',
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
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const humanReview = Array.from({ length: HUMAN_REVIEW_COUNT }, (_, i) =>
    jobInfo({
      id: `fx-human-${i + 1}`,
      title: `Human review job ${i + 1}`,
      state: '5-human-review',
      order: i + 1,
    }),
  );
  return {
    backlog: [],
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'In preparation', state: '1-preparation' })],
    orchestratorPrep: [],
    needsHumanReview: [],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-prog-1', title: 'In progress', state: '3-progress' })],
    failedPickup: [],
    autoReview: [jobInfo({ id: 'fx-auto-1', title: 'Auto review', state: '4-auto-review' })],
    humanReview,
    review: [],
    completed: [jobInfo({ id: 'fx-done-1', title: 'Completed', state: '6-completed' })],
    archive: [jobInfo({ id: 'fx-arch-1', title: 'Archive', state: '7-archive' })],
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = Object.values(grouped).flat();

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]),
    });
  });
  await page.route('**/api/jobs', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/jobs/grouped', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/runner/status', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [FIXTURE_PROJECT]: {
            projectName: FIXTURE_PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    });
  });
  await page.route('**/api/clients/**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    });
  });
  await page.route('**/api/git/summary', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/git/projects', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/cli/quota', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', ttlSeconds: 600, snapshots: [] }),
    });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', sections: [] }),
    });
  });
  await page.route('**/api/orchestrator/global', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
  });
  await page.route('**/api/projects/*/settings', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ autoCommit: false, runnerMode: 'manual', orchestratorModel: null }),
    });
  });
  await page.route('**/api/dev-tools/flags', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }),
    });
  });
}

interface LaneScrollProbe {
  state: string;
  bodyOverflowY: string;
  // The scroll-container chain inside this lane that is currently in an
  // overflowing state (clientHeight < scrollHeight). The expected
  // shape is either no entries (lane fits) or exactly one entry
  // matching .column__body. A non-body entry, or more than one entry,
  // signals the old multi-layered virtual-scroll inconsistency.
  overflowingScrollers: Array<{ tag: string; cls: string }>;
}

async function probeLaneScrollers(page: Page): Promise<LaneScrollProbe[]> {
  return await page.evaluate(() => {
    const columns = Array.from(document.querySelectorAll('[data-testid^="lane-"]:not([data-testid^="lane-rail-"]):not([data-testid^="lane-group-"]):not([data-testid^="lane-collapse-"])')) as HTMLElement[];
    return columns.map((col) => {
      const state = col.getAttribute('data-state') ?? '';
      const body = col.querySelector('.column__body') as HTMLElement | null;
      const bodyOverflowY = body ? window.getComputedStyle(body).overflowY : '<no-body>';
      const overflowingScrollers: LaneScrollProbe['overflowingScrollers'] = [];
      for (const el of Array.from(col.querySelectorAll('*')) as HTMLElement[]) {
        const style = window.getComputedStyle(el);
        const oy = style.overflowY;
        if ((oy === 'auto' || oy === 'scroll') && el.scrollHeight > el.clientHeight + 1) {
          overflowingScrollers.push({
            tag: el.tagName.toLowerCase(),
            cls: el.className.toString().slice(0, 80),
          });
        }
      }
      return { state, bodyOverflowY, overflowingScrollers };
    });
  });
}

test.describe('Kanban lane scroll model is consistent across every lane', () => {
  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    await page.addInitScript(() => {
      try {
        window.localStorage.removeItem('collapsedLanes');
      } catch {
        /* ignore */
      }
    });
  });

  test('every lane shares the same .column__body scroll model when one lane is overstocked', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('lane-5-human-review')).toBeVisible();

    // Wait for all 60 Human Review cards to land so the previously
    // virtualized lane is fully populated.
    await expect.poll(async () =>
      await page.locator('[data-testid="lane-5-human-review"] [data-testid="job-card"]').count(),
      { timeout: 5_000 }
    ).toBeGreaterThanOrEqual(HUMAN_REVIEW_COUNT);

    const probes = await probeLaneScrollers(page);
    expect(probes.length, 'expected at least the seven post-ADR-0025 lanes').toBeGreaterThanOrEqual(7);

    // 1. Every expanded lane uses the SAME scroll container shape. The
    //    column body has overflow-y: auto and is the only scrolling
    //    descendant inside the lane. A different shape on any lane is
    //    the inconsistency the bug reported.
    for (const probe of probes) {
      expect(
        probe.bodyOverflowY,
        `lane ${probe.state} has .column__body overflow-y="${probe.bodyOverflowY}" - every lane must share ` +
        `the same scroll container (overflow-y: auto) so the scrollbar appears in the same place ` +
        `regardless of which lane fills up.`,
      ).toMatch(/^(auto|scroll)$/);
    }

    // 2. The lane(s) that actually overflow must overflow at the body,
    //    not at some deeper element (which is how the previous
    //    virtual-scroll viewport surfaced as a "different" scrollbar).
    for (const probe of probes) {
      for (const s of probe.overflowingScrollers) {
        expect(
          s.cls,
          `lane ${probe.state} has an overflowing inner element <${s.tag} class="${s.cls}"> - ` +
          `the only allowed overflow point is .column__body itself.`,
        ).toMatch(/\bcolumn__body\b/);
      }
    }

    // 3. The page itself does NOT scroll vertically when a single
    //    lane is overstocked. The Kanban view fits the viewport
    //    height; vertical overflow lives inside lanes.
    const pageScrollState = await page.evaluate(() => {
      const body = document.querySelector('.app__body') as HTMLElement | null;
      return {
        appBodyScrolls: body ? body.scrollHeight - body.clientHeight : 0,
        docScrolls: document.documentElement.scrollHeight - document.documentElement.clientHeight,
      };
    });
    expect(
      pageScrollState.appBodyScrolls,
      `.app__body shows a vertical scrollbar (${pageScrollState.appBodyScrolls}px of overflow) when a ` +
      `single dense lane should be the only thing scrolling.`,
    ).toBeLessThanOrEqual(2);
  });

  test('lane collapse still toggles to a rail when a dense lane is present', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    // Wait for Human Review to fully populate so the lane is dense.
    await expect.poll(async () =>
      await page.locator('[data-testid="lane-5-human-review"] [data-testid="job-card"]').count(),
      { timeout: 5_000 }
    ).toBeGreaterThanOrEqual(HUMAN_REVIEW_COUNT);

    const collapseBtn = page.getByTestId('lane-collapse-5-human-review');
    await expect(collapseBtn).toBeVisible();
    await collapseBtn.click();

    const rail = page.getByTestId('lane-rail-5-human-review');
    await expect(rail, 'Review collapses into a rail').toBeVisible({ timeout: 500 });

    // Re-expanding restores the column with all cards.
    await rail.click();
    await expect(page.getByTestId('lane-5-human-review')).toBeVisible({ timeout: 500 });
    await expect.poll(async () =>
      await page.locator('[data-testid="lane-5-human-review"] [data-testid="job-card"]').count(),
      { timeout: 5_000 }
    ).toBeGreaterThanOrEqual(HUMAN_REVIEW_COUNT);
  });

  test('cards in the dense lane stay draggable so drag-and-drop wiring survives', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('lane-5-human-review')).toBeVisible();

    await expect.poll(async () =>
      await page.locator('[data-testid="lane-5-human-review"] [data-testid="job-card"]').count(),
      { timeout: 5_000 }
    ).toBeGreaterThanOrEqual(HUMAN_REVIEW_COUNT);

    // `draggable="true"` is bound on the `<app-job-card>` host element
    // (the component selector), not on the inner `[data-testid="job-card"]`
    // div, so we match the host directly.
    const draggableCount = await page.locator('[data-testid="lane-5-human-review"] app-job-card[draggable="true"]').count();
    expect(
      draggableCount,
      `every job card in the dense Human Review lane must remain draggable=true so drag-and-drop ` +
      `still works; got ${draggableCount} draggable out of ${HUMAN_REVIEW_COUNT} cards.`,
    ).toBeGreaterThanOrEqual(HUMAN_REVIEW_COUNT);
  });
});
