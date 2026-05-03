import { test, expect } from '@playwright/test';

/**
 * Visual & structural smoke for the slim header + bottom status bar shell.
 *
 * - The header should be short (well below the previous ~70px) so vertical
 *   space is reclaimed.
 * - The status bar must be present at the bottom and host the quick toggles
 *   (Usage / Orchestrator / Feed) and the default-CLI / default-model
 *   pickers.
 * - The picker popups should open above the bar (VS Code style) and persist
 *   the user's choice in localStorage.
 */
test.describe('Status bar and header size', () => {
  test('header is compact and status bar carries quota + pickers', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const header = page.locator('header.header');
    await expect(header).toBeVisible();
    const headerBox = await header.boundingBox();
    expect(headerBox, 'header box').not.toBeNull();
    expect(headerBox!.height).toBeLessThan(48);

    const statusBar = page.getByTestId('status-bar');
    await expect(statusBar).toBeVisible();
    const sbBox = await statusBar.boundingBox();
    expect(sbBox, 'status bar box').not.toBeNull();
    expect(sbBox!.height).toBeLessThan(40);

    // Quick toggles live in the status bar now.
    await expect(statusBar.getByTitle('CLI sessions')).toBeVisible();
    await expect(statusBar.getByTitle('Orchestrator chat')).toBeVisible();
    await expect(statusBar.getByTitle('Orchestrator feed')).toBeVisible();

    // Add Task remains the primary CTA in the header.
    await expect(header.getByRole('button', { name: /Add Task/ })).toBeVisible();

    await page.screenshot({
      path: 'test-results/status-bar-header.png',
      fullPage: false,
    });

    // Closeup of just the status bar so the picker labels read clearly.
    await statusBar.screenshot({
      path: 'test-results/status-bar-closeup.png',
    });

    // Closeup of just the header so the slim brand + tabs read clearly.
    await header.screenshot({
      path: 'test-results/header-closeup.png',
    });
  });

  test('focus / detail view layout still fits the new shell', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    // Open the first card in any column to enter focus view.
    const firstCard = page.locator('app-job-card').first();
    if (await firstCard.count()) {
      await firstCard.click();
      await page.waitForTimeout(500);
      await page.screenshot({
        path: 'test-results/status-bar-focus-view.png',
        fullPage: false,
      });
    }
  });

  test('default CLI picker persists selection', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    // Reset any previous run's state.
    await page.evaluate(() => localStorage.removeItem('defaultCliType'));
    await page.reload();
    await page.waitForTimeout(500);

    const cliPicker = page.getByTestId('status-bar-cli-picker');
    await expect(cliPicker).toContainText('Copilot');

    await cliPicker.click();
    await page.getByRole('button', { name: /Claude Code/ }).click();
    await expect(cliPicker).toContainText('Claude Code');

    const stored = await page.evaluate(() => localStorage.getItem('defaultCliType'));
    expect(stored).toBe('claude');

    await page.screenshot({
      path: 'test-results/status-bar-cli-picked.png',
      fullPage: false,
    });
  });
});
