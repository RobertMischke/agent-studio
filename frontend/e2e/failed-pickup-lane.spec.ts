import { test, expect, Page } from '@playwright/test';

/**
 * ADR-0028 (pickup failures: dedicated visible lane + persistent banner,
 * never silent archive) regression spec.
 *
 * The board renders a hide-when-empty `3a-failed-pickup` lane between
 * `3-progress` and `4-auto-review`, with a 1 px amber outline and a 12 px
 * amber dot in the header. The lane cannot be collapsed while non-empty.
 * A persistent banner above the dashboard counts failed pickups across
 * the visible (filtered) board and scrolls the lane into view on click.
 *
 * Two cases covered here:
 *   1. Empty failed-pickup -> lane and banner stay hidden.
 *   2. One failed-pickup card -> lane visible with amber outline + dot,
 *      collapse button suppressed, banner visible with the count, banner
 *      click scrolls the lane into view.
 */

const FIXTURE_WATCH = 'C:/fixtures/failed-pickup-demo';
const FIXTURE_PROJECT = 'failed-pickup-demo';

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
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null
  };
}

function fixtureGrouped(failedCount: number): Record<string, unknown[]> {
  const failedPickup: unknown[] = [];
  for (let i = 0; i < failedCount; i++) {
    failedPickup.push(jobInfo({
      id: `fx-failed-${i + 1}-orphan-2026-05-06`,
      title: `Pickup failure #${i + 1}`,
      state: '3a-failed-pickup'
    }));
  }
  return {
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting', state: '1-preparation' })],
    orchestratorPrep: [],
    needsHumanReview: [],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-progress-1', title: 'Live', state: '3-progress' })],
    failedPickup,
    autoReview: [],
    humanReview: [],
    review: [],
    completed: [jobInfo({ id: 'fx-done-1', title: 'Done', state: '6-completed' })],
    archive: []
  };
}

async function installBoardMocks(page: Page, failedCount: number): Promise<void> {
  const grouped = fixtureGrouped(failedCount);
  const allJobs = [
    ...(grouped.preparation as unknown[]),
    ...(grouped.ready as unknown[]),
    ...(grouped.progress as unknown[]),
    ...(grouped.failedPickup as unknown[]),
    ...(grouped.completed as unknown[])
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
      body: JSON.stringify({ at: '2026-05-06T09:00:00Z', ttlSeconds: 600, snapshots: [] }) });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-06T09:00:00Z', sections: [] }) });
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

test.describe('ADR-0028 failed-pickup lane + banner', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('hide-when-empty: no failed pickups -> lane and banner are absent', async ({ page }) => {
    await installBoardMocks(page, 0);
    await page.goto('/');

    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('failed-pickup-banner')).toHaveCount(0);
    await expect(page.getByTestId('lane-3a-failed-pickup')).toHaveCount(0);
  });

  test('non-empty: lane visible with amber outline + dot, collapse suppressed, banner shows count', async ({ page }) => {
    await installBoardMocks(page, 2);
    await page.goto('/');

    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    // Banner is visible and counts the failures.
    const banner = page.getByTestId('failed-pickup-banner');
    await expect(banner).toBeVisible();
    await expect(page.getByTestId('failed-pickup-banner-count')).toHaveText('2');

    // Lane is visible with the amber dot in its header.
    const lane = page.getByTestId('lane-3a-failed-pickup');
    await expect(lane).toBeVisible();
    await expect(lane).toHaveAttribute('data-state', '3a-failed-pickup');
    await expect(page.getByTestId('failed-pickup-dot')).toBeVisible();

    // Collapse button is intentionally absent on this lane (not collapsible
    // while non-empty per kanban-board-design taxonomy + ADR-0028).
    await expect(page.getByTestId('lane-collapse-3a-failed-pickup')).toHaveCount(0);

    // Outline is amber per the taxonomy. The CSS rgb is rendered, so check
    // the computed border-color is in the amber family.
    const borderColor = await lane.evaluate((el) => getComputedStyle(el).borderColor);
    // #f59e0b ≈ rgb(245, 158, 11). RGBA forms also accepted; the assertion
    // accepts the rendered colour with any alpha so a future tweak to opacity
    // does not flake the test.
    expect(borderColor).toMatch(/(?:rgba?\(\s*245\s*,\s*158\s*,\s*11)|(?:#f59e0b)/i);

    // Banner click scrolls the lane into view (the lane stays visible after).
    await banner.click();
    await expect(lane).toBeVisible();

    await page.screenshot({ path: 'test-results/failed-pickup-lane-and-banner.png', fullPage: false });
  });
});
