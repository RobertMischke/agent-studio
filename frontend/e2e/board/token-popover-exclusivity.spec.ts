import { test, expect, type Page } from '@playwright/test';

/**
 * Token-popover exclusivity + dismissal regression (AGT-2675).
 *
 * Operator screenshot on the board's Ready lane: the token-usage popovers
 * (AGT-2656) stayed rendered and stacked over multiple cards — several
 * popovers open at once, none dismissing, covering card content.
 *
 * This spec mocks the whole API surface (no live backend dependency) and
 * asserts:
 *   1. hovering a second card's token bubble closes the first card's popover
 *      — exactly one popover open at a time;
 *   2. a click outside both the trigger and the panel closes it;
 *   3. Escape closes it;
 *   4. a board data refresh (SignalR re-render) closes it, so a popover can
 *      never survive across a board refresh.
 */

const PROJECT = 'fixture-token-exclusivity';
const WATCH_PATH = 'C:/fixtures/token-exclusivity';

// The board renders `data-token-total` as input+output+cacheRead+cacheWrite
// (see `buildTokenBubble`), not the raw `tokenSummary.totalTokens` field —
// vary `inputTokens` per fixture so each card's bubble carries a distinct,
// identifiable total.
function makeJob(id: string, order: number, inputTokens = 60_000) {
  const totalTokens = inputTokens + 9_000 + 120_000 + 6_000;
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: `Token exclusivity fixture ${id}`,
    state: '2-ready',
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-08-23T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/2-ready/${id}`,
    lastActivity: '2026-08-23T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    tokenSummary: {
      calls: 2,
      inputTokens,
      outputTokens: 9_000,
      cacheReadTokens: 120_000,
      cacheCreationTokens: 6_000,
      totalTokens,
      lastModel: 'claude-opus-4-7',
      lastUpdate: '2026-08-23T08:30:00Z',
      entries: [
        { ts: '2026-08-23T08:00:00Z', model: 'claude-opus-4-7', inputTokens: 30_000, outputTokens: 4_500, cacheReadTokens: 60_000, cacheCreationTokens: 3_000 },
        { ts: '2026-08-23T08:30:00Z', model: 'claude-opus-4-7', inputTokens: 30_000, outputTokens: 4_500, cacheReadTokens: 60_000, cacheCreationTokens: 3_000 },
      ],
    },
  };
}

function grouped(jobs: ReturnType<typeof makeJob>[]) {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: jobs,
    progress: [],
    failedPickup: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    review: [],
    completed: [],
    archive: [],
  };
}

async function installRoutes(page: Page, jobs: ReturnType<typeof makeJob>[]): Promise<void> {
  // This spec runs against a real isolated backend (only /api/** is mocked),
  // so the SignalR jobs hub is genuinely reachable. Its initial-connect
  // convergence pull (`JobsHubClient.connect` -> `reconnected` -> a real,
  // unpredictably-timed `TaskService.refresh`) would otherwise race the
  // exclusivity assertions below and non-deterministically close whichever
  // popover happens to be open. Block the hub negotiate call so it never
  // connects; the board-refresh-closes-popover behavior itself is covered
  // deterministically by the `page.clock` heartbeat test further down.
  await page.route('**/hubs/jobs/**', (route) => route.abort());
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped(jobs)) }));
  await page.route('**/api/tasks/archive**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0 }) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-08-23T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-08-23T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

test.describe('Token popover exclusivity + dismissal (AGT-2675)', () => {
  test('opening a second card closes the first; outside click and Escape close it too', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await seedBoardTab(page);
    // Distinct totals (195k / 205k) so the two popovers' rendered "Total" row
    // can tell them apart after `TokenPopoverDirective` portals the open one
    // to the shared body-level overlay root — at that point it is no longer
    // a descendant of its own card, so a card-scoped locator can no longer
    // find it.
    const jobs = [makeJob('token-excl-a', 1, 60_000), makeJob('token-excl-b', 2, 70_000)];
    await installRoutes(page, jobs);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const cards = page.locator('[data-testid="task-card"]');
    await expect(cards).toHaveCount(2, { timeout: 10_000 });

    // Identify each card's trigger by its own distinct total rather than
    // board position — the lane's sort strategy does not guarantee the
    // fixtures render in insertion order.
    const bubbleA = page.locator('[data-testid="task-card-token-bubble"][data-token-total="195000"]');
    const bubbleB = page.locator('[data-testid="task-card-token-bubble"][data-token-total="205000"]');
    await expect(bubbleA).toBeVisible({ timeout: 10_000 });
    await expect(bubbleB).toBeVisible({ timeout: 10_000 });

    // Both cards render their own `<app-task-token-usage-popover>`, but only
    // the currently-open one is ever un-hidden — assert against whichever
    // instance in the whole document is visible right now.
    const visiblePopover = page.locator('[data-testid="task-card-token-popover"]:not([hidden])');
    const totalRow = () => visiblePopover.locator('[data-testid="token-row-total"]');

    await page.screenshot({ path: 'test-results/token-popover-exclusivity-before.png' });

    // 1. Hover card A, then card B without waiting — only B stays open.
    //    Before this fix both stayed open, stacked over the board.
    await bubbleA.hover();
    await expect(visiblePopover).toHaveCount(1, { timeout: 3_000 });
    await expect(totalRow()).toHaveText('195k');

    await bubbleB.hover();
    await expect(visiblePopover).toHaveCount(1, { timeout: 3_000 });
    await expect(totalRow()).toHaveText('205k');

    await page.screenshot({ path: 'test-results/token-popover-exclusivity-after-second-open.png' });

    // 2. Outside click closes the remaining open popover. Click inside the
    //    empty "Archive" lane body — away from the top-left project picker
    //    and any card/button — so this is a genuine no-op click elsewhere on
    //    the board, not an interaction with another piece of app chrome.
    await page.mouse.click(1100, 650);
    await expect(visiblePopover).toHaveCount(0, { timeout: 3_000 });

    // 3. Escape closes it too.
    await bubbleB.hover();
    await expect(visiblePopover).toHaveCount(1, { timeout: 3_000 });
    await page.keyboard.press('Escape');
    await expect(visiblePopover).toHaveCount(0, { timeout: 3_000 });

    await page.screenshot({ path: 'test-results/token-popover-exclusivity-after-dismiss.png' });
  });

  // Board data refreshes run on a 30 s heartbeat (`TaskService.HEARTBEAT_MS`)
  // in addition to SignalR push events. `page.clock` fast-forwards that timer
  // deterministically instead of waiting out the real interval or depending
  // on a live SignalR hub, while still exercising the app's real refresh
  // code path (`TaskService.refresh` -> `boardRefreshedAt` -> the popover
  // registry closing the active panel).
  test('a board data refresh closes the open popover', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await seedBoardTab(page);
    const jobs = [makeJob('token-excl-refresh', 1)];
    await installRoutes(page, jobs);
    await page.clock.install();
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const bubble = page.locator('[data-testid="task-card-token-bubble"]').first();
    const popover = page.locator('[data-testid="task-card-token-popover"]').first();
    await expect(bubble).toBeVisible({ timeout: 10_000 });

    await bubble.hover();
    await expect(popover).toBeVisible({ timeout: 3_000 });

    await page.clock.runFor(31_000);
    await expect(popover).toBeHidden({ timeout: 5_000 });

    await page.screenshot({ path: 'test-results/token-popover-exclusivity-after-heartbeat-refresh.png' });
  });
});
