import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';

/**
 * The bottom status-bar's quota strip now follows a two-step model:
 *
 * - HOVER opens a small popover (`<app-cli-usage-mini-popover>`) that
 *   shows only the core values per CLI - the current-window percentage,
 *   a meter, and the remaining headroom. It is intentionally compact and
 *   does NOT carry the full token / model-spend / top-tasks dump.
 * - CLICK (or Enter / Space) navigates into the CLI-Management overlay
 *   (`<app-cli-admin-panel>`), where the full usage detail now lives as
 *   an embedded `<app-cli-usage-detail>` section under the Settings roof.
 *
 * This spec asserts:
 * - Hovering the strip opens the compact mini-popover with per-CLI rows
 *   and the "click for full detail" hint, and stays small.
 * - Clicking the strip opens the CLI-Management overlay with the embedded
 *   full usage detail.
 * - Enter opens the overlay too (keyboard / accessibility).
 * - Escape dismisses the hover popover.
 *
 * Plus screenshots so the visual change is reviewable in chat.
 */

const SCREENSHOT_DIR = process.env.STATUS_BAR_RESULTS_DIR?.trim() || 'test-results';

test.describe('Status bar usage detail', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    // Let the first quota poll fire so the strip has cards to render.
    await page.waitForTimeout(800);
  });

  test('hovering the strip opens a compact mini-popover with core per-CLI values', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    await expect(statusBar).toBeVisible();

    // Pre-state: the popover is not in the DOM until the strip is hovered.
    await expect(page.getByTestId('usage-hover-panel-pop')).toHaveCount(0);

    const anchor = page.getByTestId('usage-hover-panel');
    await expect(anchor).toBeVisible();
    await anchor.scrollIntoViewIfNeeded();
    await anchor.hover();

    const pop = page.getByTestId('usage-hover-panel-pop');
    await expect(pop).toBeVisible({ timeout: 2_000 });

    // The compact popover renders and carries the "click for detail" hint.
    const mini = page.getByTestId('cli-usage-mini-popover');
    await expect(mini).toBeVisible();
    await expect(page.getByTestId('cli-usage-mini-hint')).toBeVisible();

    // It shows core per-CLI rows (or an explicit empty state in CI where
    // no quota has been sampled yet) - never the full detail dump.
    const rowCount = await page.getByTestId(/^cli-usage-mini-row-/).count();
    if (rowCount === 0) {
      await expect(page.getByTestId('cli-usage-mini-empty')).toBeVisible();
    }
    // The big detail must NOT be inlined in the hover popover.
    await expect(pop.getByTestId('cli-usage-detail')).toHaveCount(0);

    // The popover stays small - it is a peek, not the full panel.
    const box = await mini.boundingBox();
    expect(box, 'mini-popover box').not.toBeNull();
    expect(box!.width).toBeLessThan(360);

    // ...and it must actually sit inside the viewport. The status bar is
    // at the bottom edge, so the popover floats above the trigger; a
    // broken positioning context would push it off the top (negative y).
    const viewport = page.viewportSize();
    expect(viewport, 'viewport').not.toBeNull();
    expect(box!.y).toBeGreaterThanOrEqual(0);
    expect(box!.y + box!.height).toBeLessThanOrEqual(viewport!.height);

    // Screenshots for the chat reply.
    await page.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-mini-popover-open.png`,
      fullPage: false,
    });
    await mini.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-mini-popover-closeup.png`,
    });
  });

  test('clicking the strip opens CLI Management with the embedded full usage detail', async ({ page }) => {
    const anchor = page.getByTestId('usage-hover-panel');
    await anchor.scrollIntoViewIfNeeded();
    await anchor.click();

    const admin = page.getByTestId('cli-admin-panel');
    await expect(admin).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('cli-usage-detail')).toBeVisible();

    await page.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-cli-admin-open.png`,
      fullPage: false,
    });
  });

  test('Enter opens CLI Management too (keyboard / accessibility)', async ({ page }) => {
    const anchor = page.getByTestId('usage-hover-panel');
    await anchor.focus();
    await page.keyboard.press('Enter');

    await expect(page.getByTestId('cli-admin-panel')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('cli-usage-detail')).toBeVisible();
  });

  test('Escape dismisses the hover popover', async ({ page }) => {
    const anchor = page.getByTestId('usage-hover-panel');
    await anchor.hover();

    const pop = page.getByTestId('usage-hover-panel-pop');
    await expect(pop).toBeVisible({ timeout: 2_000 });

    await page.keyboard.press('Escape');
    await expect(pop).toHaveCount(0, { timeout: 1_000 });
  });
});
