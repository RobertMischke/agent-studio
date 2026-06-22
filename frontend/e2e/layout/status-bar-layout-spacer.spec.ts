import { test, expect } from '@playwright/test';

/**
 * StatusBar layout - quota cards in the LEFT group use the available
 * middle space, while RIGHT stays docked to the edge.
 */
test.describe('Status bar layout: dense left quota + right dock', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);
  });

  test('LEFT group contains running + auto + quota cards, CENTER is empty spacer', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    await expect(statusBar).toBeVisible();

    const left = statusBar.locator('.statusbar__group--left');
    await expect(left).toBeVisible();

    // Running and auto items are in the left group.
    await expect(left.locator('app-statusbar-item').first()).toBeVisible();

    // Quota cards are in the left group.
    await expect(left.locator('.statusbar__quota')).toBeVisible();
    await expect(left.locator('app-usage-hover-panel')).toBeVisible();

    // CENTER group exists as a compact gutter (empty flex element, no children).
    const center = statusBar.locator('.statusbar__group--center');
    await expect(center).toBeAttached();
    await expect(center).toHaveClass(/statusbar__group--spacer/);
    expect(await center.locator('*').count()).toBe(0);

    // RIGHT group has the action buttons + pickers.
    const right = statusBar.locator('.statusbar__group--right');
    await expect(right).toBeVisible();
    await expect(right.getByTestId('orch-side-sheet-toggle')).toBeVisible();
    // Per the homogenisation work (docs/frontend/audits/cli-model-selector-audit.md), the
    // two separate CLI / model pickers collapsed into the single shared
    // `<app-cli-model-selector>` chip.
    await expect(right.getByTestId('status-bar-defaults')).toBeVisible();
  });

  test('LEFT uses the middle space while RIGHT stays docked', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    const left = statusBar.locator('.statusbar__group--left');
    const right = statusBar.locator('.statusbar__group--right');

    const statusBarBox = await statusBar.boundingBox();
    const leftBox = await left.boundingBox();
    const rightBox = await right.boundingBox();

    expect(statusBarBox).not.toBeNull();
    expect(leftBox).not.toBeNull();
    expect(rightBox).not.toBeNull();

    // Left group should run close to the right dock; the bar intentionally
    // uses the middle space for per-CLI quota numbers.
    const gap = rightBox!.x - (leftBox!.x + leftBox!.width);
    expect(gap).toBeGreaterThanOrEqual(0);
    expect(gap).toBeLessThanOrEqual(12);

    // Right ends near the status bar edge even when a push panel shortens
    // the shell width.
    expect(rightBox!.x + rightBox!.width).toBeGreaterThan(statusBarBox!.x + statusBarBox!.width - 16);

    await page.screenshot({ path: 'test-results/status-bar-right-dock.png', fullPage: false });
  });

  test('responsive layout keeps status and action groups separated', async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 820 });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const statusBar = page.getByTestId('status-bar');
    const left = statusBar.locator('.statusbar__group--left');
    const right = statusBar.locator('.statusbar__group--right');

    const midLeftBox = await left.boundingBox();
    const midRightBox = await right.boundingBox();
    expect(midLeftBox).not.toBeNull();
    expect(midRightBox).not.toBeNull();
    expect(midLeftBox!.x + midLeftBox!.width).toBeLessThanOrEqual(midRightBox!.x);
    await expect(statusBar.locator('.hquota__card').first()).toBeVisible();

    await page.setViewportSize({ width: 760, height: 820 });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    await expect(statusBar.locator('.statusbar__quota')).toBeVisible();
    await expect(statusBar.locator('.hquota__card').first()).toBeVisible();
    await expect(statusBar.locator('.hquota__label').first()).toBeHidden();
    const smallLeftBox = await left.boundingBox();
    const smallRightBox = await right.boundingBox();
    expect(smallLeftBox).not.toBeNull();
    expect(smallRightBox).not.toBeNull();
    expect(smallLeftBox!.x + smallLeftBox!.width).toBeLessThanOrEqual(smallRightBox!.x);

    const hasHorizontalOverflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    expect(hasHorizontalOverflow).toBe(false);
  });
});
