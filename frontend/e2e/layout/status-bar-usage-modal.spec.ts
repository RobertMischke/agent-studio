import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';

/**
 * The bottom status-bar's quota strip exposes a hover-open detail
 * modal with the full subscription windows for every primary CLI and
 * the workspace-wide token aggregate, all in one place. Click and
 * Enter still open the modal for keyboard / touch users.
 *
 * That modal is owned by `<app-usage-hover-panel>`, which wraps the
 * existing quota strip (`<app-header-quota>`) and renders a deferred
 * dialog with subscription quota, token trend, model spend, and top tasks.
 *
 * This spec asserts:
 * - Hovering the strip opens the large modal.
 * - Clicking the strip also opens it (touch / accessibility fallback).
 * - The modal carries both the quota table and the tokens block.
 * - Esc closes the modal.
 *
 * Plus screenshots so the visual change is reviewable in chat.
 */

const SCREENSHOT_DIR = 'test-results';

test.describe('Status bar usage detail modal', () => {
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

    // The wrapper hosts the strip and listens for hover / click / keyboard open.
    const anchor = page.getByTestId('usage-hover-panel');
    await expect(anchor).toBeVisible();
    await anchor.scrollIntoViewIfNeeded();
    await anchor.hover();

    const modal = page.getByTestId('usage-hover-panel-pop');
    await expect(modal).toBeVisible({ timeout: 2_000 });

    // Both sections must be present - that is the user-visible
    // deliverable. The quota table may be empty in CI, but the tokens
    // block is always rendered.
    await expect(modal.getByTestId('usage-hover-panel-quota')).toBeVisible();
    await expect(modal.getByTestId('usage-hover-panel-tokens')).toBeVisible();
    await expect(modal.getByTestId('usage-hover-panel-top-jobs')).toBeVisible();
    await expect(page.getByTestId('hquota-modal-cli-copilot')).toBeVisible();
    await expect(page.getByTestId('hquota-modal-cli-claude')).toBeVisible();
    await expect(page.getByTestId('hquota-modal-cli-codex')).toBeVisible();

    // The modal is large enough to actually read.
    const modalBox = await modal.boundingBox();
    expect(modalBox, 'modal box').not.toBeNull();
    expect(modalBox!.width).toBeGreaterThan(500);

    // Screenshots for the chat reply.
    await page.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-usage-modal-open.png`,
      fullPage: false
    });
    await page.getByTestId('hquota-modal').screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-usage-modal-closeup.png`
    });
  });

  test('clicking the strip also opens the modal (touch / accessibility fallback)', async ({ page }) => {
    const anchor = page.getByTestId('usage-hover-panel');
    await anchor.scrollIntoViewIfNeeded();
    await anchor.click();

    const modal = page.getByTestId('usage-hover-panel-pop');
    await expect(modal).toBeVisible({ timeout: 2_000 });
  });

  test('modal supports keyboard open and close button', async ({ page }) => {
    const anchor = page.getByTestId('usage-hover-panel');
    await anchor.focus();
    await page.keyboard.press('Enter');

    const modal = page.getByTestId('usage-hover-panel-pop');
    await expect(modal).toBeVisible({ timeout: 2_000 });

    await page.getByTestId('cli-usage-detail-close').click();
    await expect(modal).toBeHidden({ timeout: 1_000 });
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
