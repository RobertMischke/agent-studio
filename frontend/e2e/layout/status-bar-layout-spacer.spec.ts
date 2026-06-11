import { test, expect } from '@playwright/test';

/**
 * F50: StatusBar layout - quota cards in the LEFT group, CENTER is empty
 * spacer, RIGHT holds action buttons + pickers.
 */
test.describe('Status bar layout: left quota + center spacer', () => {
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

    // CENTER group exists as a spacer (empty flex element, no children).
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

  test('LEFT ends before RIGHT, with breathing room between them', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    const left = statusBar.locator('.statusbar__group--left');
    const right = statusBar.locator('.statusbar__group--right');

    const leftBox = await left.boundingBox();
    const rightBox = await right.boundingBox();

    expect(leftBox).not.toBeNull();
    expect(rightBox).not.toBeNull();

    // Left group ends well before right group starts (spacer provides gap).
    const gap = rightBox!.x - (leftBox!.x + leftBox!.width);
    expect(gap).toBeGreaterThan(20);

    // Right ends near the viewport edge (within padding).
    expect(rightBox!.x + rightBox!.width).toBeGreaterThan(1580);

    await page.screenshot({
      path: 'test-results/f50-status-bar-layout-spacer.png',
      fullPage: false,
    });
  });
});
