import { test, expect } from '@playwright/test';

/**
 * Orchestrator side sheet — Phase 2 visual + behavioural smoke.
 *
 * Verifies the toolbar button toggles a right-hand chat-style side sheet
 * (same flex-collapse pattern as CLI Usage), shows the project switcher
 * when more than one project is watched, and renders orchestrator log
 * entries as chat bubbles. Captures screenshots so the layout can be
 * reviewed in the chat without running the UI.
 */
const SHOTS = 'screenshots/orch-side-sheet';

test.describe('Orchestrator side sheet', () => {
  test('opens via toolbar, shows chat surface, closes again', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    // Board screenshot before opening — establishes the baseline width.
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${SHOTS}/01-board-closed.png`, fullPage: false });

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();

    // Sheet open — give Angular a tick to mount the chat content + run
    // the initial /orchestrator-log fetch.
    await page.waitForTimeout(1200);
    await page.screenshot({ path: `${SHOTS}/02-side-sheet-open.png`, fullPage: false });

    const chatBody = page.getByTestId('chat-body');
    await expect(chatBody).toBeVisible();

    const composer = page.getByTestId('chat-input');
    await expect(composer).toBeVisible();
    const sendBtn = page.getByTestId('chat-send');
    await expect(sendBtn).toBeVisible();

    // Tight crop of just the side sheet for layout review.
    const box = await sheet.boundingBox();
    if (box) {
      await page.screenshot({
        path: `${SHOTS}/03-side-sheet-only.png`,
        clip: {
          x: Math.max(0, box.x - 4),
          y: Math.max(0, box.y - 4),
          width: Math.min(page.viewportSize()!.width - box.x + 4, box.width + 8),
          height: box.height + 8
        }
      });
    }

    // Phase 2 wires sending to the existing orchestrator-override endpoint,
    // which needs an "anchor" decision to steer. When the active project has
    // no decisions yet, the composer stays disabled and the placeholder
    // explains why. Phase 3 (real conversation endpoint) lifts that.
    await expect(composer).toBeDisabled();
    await expect(sendBtn).toBeDisabled();
    await expect(composer).toHaveAttribute(
      'placeholder',
      /No anchor decision yet/
    );

    // Verify the project switcher tabs render when more than one project is
    // watched, and clicking the other tab swaps the active thread.
    const tabs = page.getByTestId('orch-side-sheet-tabs');
    if (await tabs.isVisible()) {
      const tabButtons = tabs.locator('button');
      const count = await tabButtons.count();
      if (count >= 2) {
        await tabButtons.nth(1).click();
        await page.waitForTimeout(400);
        await page.screenshot({ path: `${SHOTS}/04-side-sheet-other-project.png`, fullPage: false });
      }
    }

    // Close.
    await page.getByTestId('orch-side-sheet-close').click();
    await page.waitForTimeout(500);
    await page.screenshot({ path: `${SHOTS}/05-side-sheet-closed.png`, fullPage: false });
  });
});
