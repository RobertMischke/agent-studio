import { test, expect, type Page } from '@playwright/test';

/**
 * Token-popover open/close + viewport regression (ASS-1700).
 *
 * Bug: the portal migration dropped the native `popover` attribute that kept
 * the token-usage panel collapsed by default. Every card then painted its
 * `position: fixed` popover at its static position (off the right edge of the
 * viewport), clipped, and hung permanently open across multiple cards.
 *
 * This spec mocks the whole API surface so it runs against any served frontend
 * (no live backend). It asserts:
 *   1. the popover is NOT shown until the trigger is hovered;
 *   2. once open it is portaled into the body overlay root and sits fully
 *      inside the viewport (no right-edge cutoff);
 *   3. it closes again when the pointer leaves.
 */

const PROJECT = 'fixture-token-viewport';
const WATCH_PATH = 'C:/fixtures/token-viewport';

function makeJob() {
  return {
    id: 'token-viewport-job',
    taskKey: `${WATCH_PATH}::token-viewport-job`,
    title: 'Token viewport fixture',
    state: '5-human-review',
    order: 1,
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
  ready: [],
  progress: [],
  failedPickup: [],
  autoReview: [],
  humanReview: [makeJob()],
  escalated: [],
  review: [],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
  await page.route('**/api/tasks/archive**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0 }) }));
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));
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
  test('stays collapsed until hover, then opens fully inside the viewport', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const card = page.locator('[data-testid="task-card"]').first();
    await expect(card).toBeVisible({ timeout: 10_000 });

    const bubble = card.locator('[data-testid="task-card-token-bubble"]');
    await expect(bubble).toBeVisible({ timeout: 5_000 });

    // 1. The popover must not be visible before any interaction (the bug was a
    //    permanently-open, clipped panel).
    const popover = page.locator('[data-testid="task-card-token-popover"]');
    await expect(popover).toBeHidden();

    // Capture the resting board (no panel hanging open).
    await page.screenshot({ path: 'test-results/token-popover-default-hidden.png' });

    // 2. Hover the trigger -> popover opens, portaled to the body overlay root.
    await bubble.hover();
    await expect(popover).toBeVisible({ timeout: 3_000 });
    const overlayRoot = page.locator('[data-testid="studio-overlay-root"]');
    await expect(overlayRoot.locator('[data-testid="task-card-token-popover"]')).toBeVisible();

    // 3. It sits fully inside the viewport: no right-edge cutoff, no left spill.
    const box = await popover.boundingBox();
    expect(box, 'popover should have a layout box when open').not.toBeNull();
    if (box) {
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(1280);
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.y + box.height).toBeLessThanOrEqual(800);
    }

    await page.screenshot({ path: 'test-results/token-popover-open-in-viewport.png' });

    // 4. Closes again when the pointer leaves (move to a far corner).
    await page.mouse.move(10, 10);
    await expect(popover).toBeHidden({ timeout: 3_000 });
  });
});
