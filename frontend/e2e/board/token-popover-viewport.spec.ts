import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Token-popover open/close + viewport regression (ASS-1700).
 *
 * Bug: the portal migration dropped the native `popover` attribute that kept
 * the token-usage panel collapsed by default. Every card then painted its
 * `position: fixed` popover at its static position (off the right edge of the
 * viewport), clipped, and hung permanently open across multiple cards.
 *
 * This spec mocks the whole API surface so it runs against any served frontend
 * (no live backend). The Ready lane deliberately carries 35 token-bearing
 * cards, matching the dense operator lane where the regression surfaced.
 */

const PROJECT = 'fixture-token-viewport';
const WATCH_PATH = 'C:/fixtures/token-viewport';

const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim() || 'test-results';

function makeJob(index: number) {
  const suffix = String(index + 1).padStart(2, '0');
  return {
    id: `token-viewport-job-${suffix}`,
    taskKey: `${WATCH_PATH}::token-viewport-job-${suffix}`,
    title: `Token viewport fixture ${suffix}`,
    state: '2-ready',
    order: index + 1,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-06-09T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/token-viewport-job`,
    lastActivity: '2026-06-09T09:00:00Z',
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
      calls: 3,
      inputTokens: 120_000,
      outputTokens: 18_000,
      cacheReadTokens: 250_000,
      cacheCreationTokens: 12_000,
      totalTokens: 400_000,
      lastModel: 'claude-opus-4-7',
      lastUpdate: '2026-06-09T08:30:00Z',
      entries: [
        { ts: '2026-06-09T08:00:00Z', model: 'claude-opus-4-7', inputTokens: 50_000, outputTokens: 6_000, cacheReadTokens: 100_000, cacheCreationTokens: 4_000 },
        { ts: '2026-06-09T08:15:00Z', model: 'claude-opus-4-7', inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 80_000, cacheCreationTokens: 4_000 },
        { ts: '2026-06-09T08:30:00Z', model: 'claude-opus-4-7', inputTokens: 30_000, outputTokens: 6_000, cacheReadTokens: 70_000, cacheCreationTokens: 4_000 },
      ],
    },
  };
}

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: Array.from({ length: 35 }, (_, index) => makeJob(index)),
  progress: [],
  failedPickup: [],
  autoReview: [],
  humanReview: [],
  escalated: [],
  review: [],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page): Promise<{ groupedRequests: number }> {
  const requests = { groupedRequests: 0 };
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/tasks/grouped**', (route) => {
    requests.groupedRequests += 1;
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) });
  });
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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-09T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-09T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
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
  return requests;
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

test.describe('Token popover open/close + viewport (ASS-1700)', () => {
  test('keeps one refresh-safe popover across a dense Ready lane', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await seedBoardTab(page);
    const requests = await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const cards = page.getByTestId('lane-2-ready').getByTestId('task-card');
    await expect(cards).toHaveCount(35, { timeout: 10_000 });
    const popovers = page.getByTestId('task-card-token-popover');
    await expect(popovers).toHaveCount(35);
    const visiblePopovers = page.locator('[data-testid="task-card-token-popover"]:visible');

    // Dense-lane rest state: all panel templates exist but none paints before
    // an explicit hover, focus, or click on a token chip.
    await expect(visiblePopovers).toHaveCount(0);

    const firstCard = cards.filter({ hasText: 'Token viewport fixture 01' });
    const secondCard = cards.filter({ hasText: 'Token viewport fixture 02' });
    const firstBubble = firstCard.getByTestId('task-card-token-bubble');
    const secondBubble = secondCard.getByTestId('task-card-token-bubble');
    await firstBubble.hover();
    await expect(visiblePopovers).toHaveCount(1, { timeout: 3_000 });

    // Keyboard-open the second chip while the pointer remains over the first.
    // Before the coordinator fix this leaves both panels rendered and stacked.
    await secondBubble.focus();
    const sequentialOpenCount = await visiblePopovers.count();
    if (sequentialOpenCount > 1) {
      mkdirSync(RESULTS_DIR, { recursive: true });
      await page.screenshot({
        path: join(RESULTS_DIR, 'token-popover-stacked-before--mocked.png'),
        fullPage: false,
      });
    }
    await expect(visiblePopovers).toHaveCount(1);

    // The surviving panel is portaled into the shared overlay layer and remains
    // clamped to the viewport.
    const popover = visiblePopovers.first();
    const overlayRoot = page.locator('[data-testid="studio-overlay-root"]');
    await expect(overlayRoot.locator('[data-testid="task-card-token-popover"]')).toBeVisible();
    const box = await popover.boundingBox();
    expect(box, 'popover should have a layout box when open').not.toBeNull();
    if (box) {
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(1280);
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.y + box.height).toBeLessThanOrEqual(800);
    }

    mkdirSync(RESULTS_DIR, { recursive: true });
    await page.screenshot({
      path: join(RESULTS_DIR, 'token-popover-single-after--mocked.png'),
      fullPage: false,
    });

    // Escape dismisses without moving focus.
    await page.keyboard.press('Escape');
    await expect(visiblePopovers).toHaveCount(0);

    // A programmatic refresh click exercises the same grouped snapshot signal
    // used by SignalR reconciliation without producing an outside pointerdown.
    await secondBubble.click();
    await expect(visiblePopovers).toHaveCount(1);
    const groupedRequestsBeforeRefresh = requests.groupedRequests;
    await page.getByTestId('studio-sidebar-refresh').evaluate((button: HTMLButtonElement) => button.click());
    await expect.poll(() => requests.groupedRequests).toBeGreaterThan(groupedRequestsBeforeRefresh);
    await expect(visiblePopovers).toHaveCount(0);

    // Outside click and lane scroll each dismiss an independently reopened panel.
    await secondBubble.click();
    await expect(visiblePopovers).toHaveCount(1);
    await page.getByTestId('lane-title-2-ready').click();
    await expect(visiblePopovers).toHaveCount(0);

    await secondBubble.click();
    await expect(visiblePopovers).toHaveCount(1);
    await page.getByTestId('lane-body-2-ready').evaluate((lane) => lane.scrollBy(0, 400));
    await expect(visiblePopovers).toHaveCount(0);
  });
});
