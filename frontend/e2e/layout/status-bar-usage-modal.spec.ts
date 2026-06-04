import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';

/**
 * The bottom status-bar's quota strip now follows a single model:
 *
 * - There is NO hover tooltip / hover popover any more. Hovering a CLI
 *   card does nothing but a subtle highlight.
 * - CLICKING a CLI card opens THAT CLI's own usage-detail modal
 *   (`<app-cli-usage-modal>`, testid `cli-usage-modal-<cli>`). One modal
 *   per CLI — never a grouped multi-CLI view.
 * - The modal lists every quota window the probe reported (so Claude /
 *   Codex show both their 5h and weekly windows) plus that CLI's top
 *   models, and its "Manage usage caps" footer drops into the full
 *   CLI-Management panel where caps are edited.
 *
 * This spec asserts:
 * - The old hover popover is gone (never appears, opens no modal).
 * - Clicking a card opens only that CLI's modal.
 * - Escape and backdrop-click both close the modal.
 * - "Manage usage caps" opens the CLI-Management overlay.
 *
 * Plus screenshots so the visual change is reviewable in chat.
 */

const SCREENSHOT_DIR = process.env.STATUS_BAR_RESULTS_DIR?.trim() || 'test-results';
const CLIS = ['copilot', 'claude', 'codex'] as const;

test.describe('Status bar usage modal', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    // Let the first quota poll fire so the strip has cards to render.
    await page.waitForTimeout(800);
  });

  test('the strip has no hover popover / hover tooltip', async ({ page }) => {
    const strip = page.getByTestId('usage-hover-panel');
    await expect(strip).toBeVisible();
    await strip.scrollIntoViewIfNeeded();
    await strip.hover();
    await page.waitForTimeout(400);

    // The old hover popover and mini-popover are gone for good.
    await expect(page.getByTestId('usage-hover-panel-pop')).toHaveCount(0);
    await expect(page.getByTestId('cli-usage-mini-popover')).toHaveCount(0);
    // Hovering must not open any modal either.
    await expect(page.locator('[data-testid^="cli-usage-modal-"]')).toHaveCount(0);
  });

  test('both 5h and weekly windows show on Claude / Codex cards', async ({ page }) => {
    // Requirement #2: every reported window is surfaced in the strip, so
    // Claude and Codex carry both a 5h and a weekly chip. In CI with no
    // sampled quota the cards fall back to a single placeholder chip, so
    // only assert the structure when real data is present.
    for (const cli of ['claude', 'codex'] as const) {
      const fiveH = page.getByTestId(`hquota-${cli}-5h`);
      const weekly = page.getByTestId(`hquota-${cli}-wk`);
      if ((await fiveH.count()) > 0) {
        await expect(fiveH).toBeVisible();
        await expect(weekly).toBeVisible();
      }
    }

    // Close-up of the strip so the two-chip layout is reviewable in chat.
    const strip = page.getByTestId('usage-hover-panel');
    await strip.scrollIntoViewIfNeeded();
    await strip.screenshot({ path: `${SCREENSHOT_DIR}/status-bar-strip-windows.png` });
  });

  test("clicking a CLI card opens that CLI's own modal (and only that CLI)", async ({ page }) => {
    const claudeCard = page.getByTestId('hquota-card-claude');
    await expect(claudeCard).toBeVisible();
    await claudeCard.scrollIntoViewIfNeeded();
    await claudeCard.click();

    const modal = page.getByTestId('cli-usage-modal-claude');
    await expect(modal).toBeVisible({ timeout: 4_000 });

    // One modal per CLI: the codex / copilot modals must NOT be present,
    // and the grouped multi-CLI detail surface is not in the click flow.
    await expect(page.getByTestId('cli-usage-modal-codex')).toHaveCount(0);
    await expect(page.getByTestId('cli-usage-modal-copilot')).toHaveCount(0);
    await expect(page.getByTestId('cli-usage-detail')).toHaveCount(0);

    await page.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-cli-modal-claude.png`,
      fullPage: false,
    });

    await page.keyboard.press('Escape');
    await expect(modal).toHaveCount(0, { timeout: 2_000 });
  });

  test('each CLI card opens its matching modal; backdrop closes it', async ({ page }) => {
    for (const cli of CLIS) {
      const card = page.getByTestId(`hquota-card-${cli}`);
      await expect(card).toBeVisible();
      await card.click();

      const modal = page.getByTestId(`cli-usage-modal-${cli}`);
      await expect(modal).toBeVisible({ timeout: 4_000 });

      // Backdrop click (top-left corner, clear of the centred panel) closes.
      await page.getByTestId(`cli-usage-modal-${cli}-overlay`).click({ position: { x: 6, y: 6 } });
      await expect(modal).toHaveCount(0, { timeout: 2_000 });
    }
  });

  test('the modal\'s "Manage usage caps" opens the CLI-Management panel', async ({ page }) => {
    await page.getByTestId('hquota-card-claude').click();
    await expect(page.getByTestId('cli-usage-modal-claude')).toBeVisible({ timeout: 4_000 });

    await page.getByTestId('cli-usage-modal-manage-caps').click();
    await expect(page.getByTestId('cli-admin-panel')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('cli-usage-detail')).toBeVisible();
  });
});
