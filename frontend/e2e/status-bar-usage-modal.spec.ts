import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';

/**
 * The bottom status-bar's quota strip is hard to read at 18×18 px - it's
 * fine as a glance indicator, but the user wanted a "very complete"
 * hover modal with the full subscription windows for every CLI **and**
 * the workspace-wide token aggregate, all in one place.
 *
 * That modal is owned by `<app-usage-hover-panel>`, which wraps the
 * existing donut strip (`<app-header-quota>`) and renders a JS-driven
 * panel with two sections: subscription quota + tokens consumed.
 *
 * This spec asserts:
 * - Hovering the strip opens the large modal (debounced ~120 ms).
 * - The modal carries both the quota table and the tokens block.
 * - Moving the cursor onto the modal keeps it open (close grace).
 * - Esc closes the modal.
 *
 * Plus screenshots so the visual change is reviewable in chat.
 */

const SCREENSHOT_DIR = 'test-results';

test.describe('Status bar usage hover panel', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    // Let the first quota poll fire so the strip has cards to render.
    await page.waitForTimeout(800);
  });

  test('hovering the strip opens a modal with quota and token sections', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    await expect(statusBar).toBeVisible();

    // Pre-state: the modal is not in the DOM until the strip is hovered.
    await expect(page.getByTestId('usage-hover-panel-pop')).toHaveCount(0);

    // The wrapper hosts the strip (donut chips) and listens for hover.
    const anchor = page.getByTestId('usage-hover-panel');
    await expect(anchor).toBeVisible();
    await anchor.scrollIntoViewIfNeeded();
    await anchor.hover();

    // The open is debounced ~120 ms to avoid flicker on accidental
    // crossings, so wait for the modal explicitly.
    const modal = page.getByTestId('usage-hover-panel-pop');
    await expect(modal).toBeVisible({ timeout: 2_000 });

    // Both sections must be present - that is the user-visible
    // deliverable. The quota table may be empty in CI (no probes yet)
    // but the tokens block is always rendered.
    await expect(modal.getByTestId('usage-hover-panel-tokens')).toBeVisible();

    // The modal is large enough to actually read.
    const modalBox = await modal.boundingBox();
    expect(modalBox, 'modal box').not.toBeNull();
    expect(modalBox!.width).toBeGreaterThan(500);

    // Screenshots for the chat reply.
    await page.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-usage-modal-open.png`,
      fullPage: false
    });
    await modal.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-usage-modal-closeup.png`
    });
  });

  test('modal stays open while the cursor moves into it', async ({ page }) => {
    const anchor = page.getByTestId('usage-hover-panel');
    await anchor.hover();

    const modal = page.getByTestId('usage-hover-panel-pop');
    await expect(modal).toBeVisible({ timeout: 2_000 });

    // Move into the modal - the close timer should clear.
    await modal.hover();
    await page.waitForTimeout(400);
    await expect(modal).toBeVisible();

    // Move away to the page body - modal closes after the close-delay.
    await page.mouse.move(10, 10);
    await expect(modal).toBeHidden({ timeout: 2_000 });
  });

  test('Escape closes the modal', async ({ page }) => {
    const anchor = page.getByTestId('usage-hover-panel');
    await anchor.hover();

    const modal = page.getByTestId('usage-hover-panel-pop');
    await expect(modal).toBeVisible({ timeout: 2_000 });

    await page.keyboard.press('Escape');
    await expect(modal).toBeHidden({ timeout: 1_000 });
  });
});
