import { test, expect } from '@playwright/test';

/**
 * F59 - Filter-active badge on the activity-bar icon, "Clear all"
 * button in the filter-panel header, and empty-state banner above
 * the board when all tasks are filtered away.
 */
test.describe('Filter-active badge (F59)', () => {
  async function openBoard(page: import('@playwright/test').Page): Promise<void> {
    const trigger = page.getByTestId('studio-project-picker-trigger');
    await expect(trigger).toBeVisible({ timeout: 10_000 });
    await trigger.click();
    const allItem = page.getByTestId('studio-project-picker-item-__all__');
    await expect(allItem).toBeVisible({ timeout: 5000 });
    await allItem.click();
    await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 10_000 });
  }

  test.beforeEach(async ({ page }) => {
    // Stub the UpdateService status endpoint so a stuck rollback modal
    // never blocks the UI (the update service runs on port 5039).
    await page.route('**/update/status', route =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ phase: 'idle', isRunning: false, behindBy: 0 }),
      })
    );

    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
    });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(2000);
    await openBoard(page);
  });

  test('activity-bar filter icon shows badge when a filter is active', async ({ page }) => {
    const filterBtn = page.getByTestId('studio-ab-filters');
    await expect(filterBtn).toBeVisible();

    const badge = page.getByTestId('studio-ab-badge-filters');
    await expect(badge).toHaveCount(0);

    await filterBtn.click();
    await expect(page.getByTestId('studio-sidebar')).toBeVisible();

    await page.getByTestId('kanban-filter-type-bug').click();

    await expect(badge).toBeVisible();
    await expect(badge).toHaveText('1');
  });

  test('project scope does not count as an active filter', async ({ page }) => {
    const trigger = page.getByTestId('studio-project-picker-trigger');
    await expect(trigger).toBeVisible();
    await trigger.click();

    const projectItems = page
      .locator('[data-testid^="studio-project-picker-item-"]')
      .filter({ hasNotText: 'All projects' });
    const projectCount = await projectItems.count();
    test.skip(projectCount === 0, 'No project item available to scope the board.');

    await projectItems.first().click();
    await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 10_000 });

    // AGT-2035: the compact-visibility toggle was removed (card density abolished);
    // project scope alone must still not register as an active filter.
    await expect(page.getByTestId('studio-ab-badge-filters')).toHaveCount(0);

    await page.getByTestId('studio-ab-filters').click();
    await expect(page.getByTestId('studio-sidebar')).toBeVisible();
    await page.getByTestId('kanban-filter-type-bug').click();

    const badge = page.getByTestId('studio-ab-badge-filters');
    await expect(badge).toBeVisible();
    await expect(badge).toHaveText('1');
  });

  test('badge tooltip shows filter count', async ({ page }) => {
    const filterBtn = page.getByTestId('studio-ab-filters');
    await filterBtn.click();

    await page.getByTestId('kanban-filter-type-bug').click();
    const badge = page.getByTestId('studio-ab-badge-filters');
    await expect(badge).toBeVisible();

    await badge.hover();
    const tooltip = page.getByTestId('cac-tooltip');
    await expect(tooltip).toBeVisible({ timeout: 3000 });
    await expect(tooltip).toContainText('filter');
    await expect(tooltip).toContainText('active');
  });

  test('empty-state banner appears when search produces zero matches', async ({ page }) => {
    const filterBtn = page.getByTestId('studio-ab-filters');
    await filterBtn.click();

    const searchInput = page.getByTestId('kanban-filter-sidesheet-search');
    await expect(searchInput).toBeVisible();
    await searchInput.fill('zzz_no_match_f59_' + Date.now());

    const banner = page.getByTestId('board-filter-empty-hint');
    await expect(banner).toBeVisible({ timeout: 5000 });
    await expect(banner).toContainText('0 tasks for filter Search:');

    const clearBtn = page.getByTestId('board-filter-empty-hint-clear');
    await expect(clearBtn).toBeVisible();
    await clearBtn.click({ force: true });

    await expect(banner).toHaveCount(0);
  });

  test('empty-state banner shows filter count when faceted filter active', async ({ page }) => {
    const filterBtn = page.getByTestId('studio-ab-filters');
    await filterBtn.click();

    const searchInput = page.getByTestId('kanban-filter-sidesheet-search');
    await searchInput.fill('zzz_no_match_f59_' + Date.now());
    await page.getByTestId('kanban-filter-type-bug').click();

    const banner = page.getByTestId('board-filter-empty-hint');
    await expect(banner).toBeVisible({ timeout: 5000 });
    await expect(banner).toContainText('0 tasks for filters Search:');
    await expect(banner).toContainText('Type:');
  });

  test('sidebar header shows "Clear all" button when filters are active', async ({ page }) => {
    const filterBtn = page.getByTestId('studio-ab-filters');
    await filterBtn.click();

    const clearAll = page.getByTestId('studio-sidebar-filter-clear-all');
    await expect(clearAll).toHaveCount(0);

    await page.getByTestId('kanban-filter-type-bug').click();

    await expect(clearAll).toBeVisible();
    await expect(clearAll).toHaveText('Clear all');

    await clearAll.click();

    await expect(clearAll).toHaveCount(0);

    const badge = page.getByTestId('studio-ab-badge-filters');
    await expect(badge).toHaveCount(0);
  });

  test('badge disappears after clearing filters via sidebar header', async ({ page }) => {
    const filterBtn = page.getByTestId('studio-ab-filters');
    await filterBtn.click();

    await page.getByTestId('kanban-filter-type-bug').click();

    const badge = page.getByTestId('studio-ab-badge-filters');
    await expect(badge).toBeVisible();

    const headerClear = page.getByTestId('studio-sidebar-filter-clear-all');
    await expect(headerClear).toBeVisible();
    await headerClear.click();

    await expect(badge).toHaveCount(0);
  });
});
