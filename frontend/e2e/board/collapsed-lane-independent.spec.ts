import { test, expect, type Page } from '@playwright/test';

/**
 * Regression spec: collapsing ONE lane must NOT cascade to other lanes,
 * and the collapsed rail must show the lane's identity (icon, title, count).
 *
 * Covers two reported bugs:
 *  1. Cascading collapse: clicking collapse on one lane collapsed all lanes below it.
 *  2. Lost identity: collapsed rail showed only a number and caret, no name or icon.
 *
 * Runs in the studio (VS Code) layout which stacks lanes vertically within
 * super-columns. The rail renders as a horizontal strip in this layout.
 */

const PROJECT = 'fixture-collapse-test';
const WATCH_PATH = 'C:/fixtures/collapse-test';

function makeJob(id: string, state: string, order: number) {
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: `Job ${id}`,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-27T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-27T09:00:00Z',
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
  };
}

const GROUPED = {
  backlog: [],
  preparation: [makeJob('prep-1', '1-preparation', 1)],
  orchestratorPrep: [makeJob('op-1', '1a-orchestrator-prep', 1)],
  needsHumanReview: [],
  ready: [makeJob('ready-1', '2-ready', 1), makeJob('ready-2', '2-ready', 2)],
  progress: [makeJob('prog-1', '3-progress', 1)],
  failedPickup: [],
  review: [],
  autoReview: [makeJob('ar-1', '4-auto-review', 1)],
  humanReview: [makeJob('hr-1', '5-human-review', 1)],
  completed: [makeJob('done-1', '6-completed', 1)],
  archive: [makeJob('arch-1', '7-archive', 1)],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));

  await page.route('**/api/jobs/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));

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
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-27T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-27T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
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
    localStorage.removeItem('collapsedLanes');
  });
}

async function waitForBoard(page: Page): Promise<void> {
  await expect(page.locator('[data-testid="studio-board"]').first())
    .toBeVisible({ timeout: 10_000 });
  await expect(page.locator('[data-testid="job-card"]').first()).toBeVisible({ timeout: 10_000 });
  // Dismiss any toast notification that might overlay the board.
  const dismiss = page.getByTestId('toast-dismiss').or(page.getByRole('button', { name: 'Dismiss' }));
  if (await dismiss.count() > 0) {
    await dismiss.first().click({ timeout: 1_000 }).catch(() => undefined);
    await page.waitForTimeout(200);
  }
}

test.describe('Collapsed lane: no cascade + identity preserved', () => {

  test('collapsing one lane does NOT cascade to siblings', async ({ page }, testInfo) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);

    // Screenshot: all lanes expanded.
    const beforePng = await page.screenshot({ fullPage: false });
    await testInfo.attach('before-collapse.png', { body: beforePng, contentType: 'image/png' });

    // Identify lanes that should stay expanded.
    const stateToCollapse = '3-progress';
    const siblingStates = ['2-ready', '4-auto-review', '5-human-review', '6-completed'];

    // Verify all lanes are expanded before collapsing.
    for (const s of [stateToCollapse, ...siblingStates]) {
      await expect(
        page.getByTestId(`lane-${s}`),
        `lane ${s} should be expanded before collapse`
      ).toBeVisible({ timeout: 3_000 });
    }

    // Collapse In Progress.
    const collapseBtn = page.getByTestId(`lane-collapse-${stateToCollapse}`);
    await expect(collapseBtn).toBeVisible({ timeout: 3_000 });
    await collapseBtn.click();

    // The collapsed lane should show its rail.
    const rail = page.getByTestId(`lane-rail-${stateToCollapse}`);
    await expect(rail).toBeVisible({ timeout: 2_000 });

    // Every sibling lane must still be expanded (NOT collapsed).
    for (const s of siblingStates) {
      await expect(
        page.getByTestId(`lane-${s}`),
        `lane ${s} must NOT cascade-collapse when ${stateToCollapse} is collapsed`
      ).toBeVisible({ timeout: 2_000 });
    }

    // Screenshot: only one lane collapsed.
    const afterPng = await page.screenshot({ fullPage: false });
    await testInfo.attach('after-collapse-one.png', { body: afterPng, contentType: 'image/png' });

    if (process.env.RESULTS_DIR) {
      const fs = await import('fs');
      const path = await import('path');
      fs.mkdirSync(process.env.RESULTS_DIR, { recursive: true });
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'before-collapse.png'), beforePng);
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'after-collapse-one.png'), afterPng);
    }
  });

  test('collapsed rail shows lane identity: icon, title, and count', async ({ page }, testInfo) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);

    // Collapse the In Progress lane.
    const collapseBtn = page.getByTestId('lane-collapse-3-progress');
    await expect(collapseBtn).toBeVisible({ timeout: 3_000 });
    await collapseBtn.click();

    const rail = page.getByTestId('lane-rail-3-progress');
    await expect(rail).toBeVisible({ timeout: 2_000 });

    // The icon should be visible.
    const icon = rail.locator('.column-rail__icon');
    await expect(icon).toBeVisible();
    const iconText = await icon.textContent();
    expect(iconText?.trim()).toBeTruthy();

    // The title should be visible and contain the lane name.
    const title = rail.locator('.column-rail__title');
    await expect(title).toBeVisible();
    const titleText = await title.textContent();
    expect(titleText?.trim().toLowerCase()).toContain('progress');

    // The title must have non-zero rendered dimensions.
    const titleBox = await title.boundingBox();
    expect(titleBox, 'rail title must have a bounding box').toBeTruthy();
    expect(titleBox!.width, 'rail title width must be > 0').toBeGreaterThan(0);
    expect(titleBox!.height, 'rail title height must be > 0').toBeGreaterThan(0);

    // The count should be visible.
    const count = rail.locator('.column-rail__count');
    await expect(count).toBeVisible();
    const countText = await count.textContent();
    expect(countText?.trim()).toBe('1');

    // Screenshot.
    const png = await page.screenshot({ fullPage: false });
    await testInfo.attach('rail-identity.png', { body: png, contentType: 'image/png' });

    if (process.env.RESULTS_DIR) {
      const fs = await import('fs');
      const path = await import('path');
      fs.mkdirSync(process.env.RESULTS_DIR, { recursive: true });
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'rail-identity.png'), png);
    }
  });

  test('collapse state persists across reload', async ({ page }) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);

    // Collapse two lanes.
    for (const s of ['3-progress', '7-archive']) {
      const btn = page.getByTestId(`lane-collapse-${s}`);
      if ((await btn.count()) > 0 && await btn.isVisible()) {
        await btn.click();
        await expect(page.getByTestId(`lane-rail-${s}`)).toBeVisible({ timeout: 2_000 });
      }
    }

    // Verify localStorage was written.
    const stored = await page.evaluate(() => localStorage.getItem('collapsedLanes'));
    expect(stored).toBeTruthy();
    const parsed = JSON.parse(stored!);
    expect(parsed).toContain('3-progress');

    // Reload and verify persistence.
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);

    // The previously collapsed lane should still be collapsed.
    await expect(page.getByTestId('lane-rail-3-progress')).toBeVisible({ timeout: 5_000 });

    // Lanes that were NOT collapsed should still be expanded.
    await expect(page.getByTestId('lane-2-ready')).toBeVisible({ timeout: 5_000 });
  });
});
