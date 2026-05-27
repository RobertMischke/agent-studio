import { test, expect } from '@playwright/test';

/**
 * Evidence screenshots for the status-bar picker migration to <app-menu>.
 * Captures both CLI and model pickers opened, showing they now render through
 * the shared menu surface with proper tokens, keyboard nav, and ARIA.
 */
test.describe('Status bar menu migration evidence', () => {
  test('CLI picker opens via shared app-menu with keyboard nav', async ({ page }) => {
    await page.route('**/api/cli/*/models*', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          models: [
            { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', isDefault: true, multiplier: 1 },
            { id: 'claude-opus-4-7', label: 'Opus 4.7', isDefault: false, multiplier: 5 },
            { id: 'claude-haiku-4-5', label: 'Haiku 4.5', isDefault: false, multiplier: 0.2 },
          ],
          defaultModel: 'claude-sonnet-4-6',
          source: 'mock',
        }),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    // Open CLI picker
    const cliPicker = page.getByTestId('status-bar-cli-picker');
    await cliPicker.click();

    const cliMenu = page.getByTestId('status-bar-cli-menu-panel');
    await expect(cliMenu).toBeVisible();
    await expect(cliMenu).toHaveAttribute('role', 'menu');

    // Verify ARIA on rows
    const firstRow = cliMenu.locator('[role="menuitem"]').first();
    await expect(firstRow).toBeVisible();

    // Keyboard: arrow down moves focus
    await cliMenu.press('ArrowDown');
    await cliMenu.press('ArrowDown');

    await page.screenshot({
      path: 'test-results/status-bar-cli-menu-open.png',
      fullPage: false,
    });

    // Escape closes
    await cliMenu.press('Escape');
    await expect(cliMenu).not.toBeVisible();

    // Now open model picker
    const modelPicker = page.getByTestId('status-bar-model-picker');
    await modelPicker.click();

    const modelMenu = page.getByTestId('status-bar-model-menu-panel');
    await expect(modelMenu).toBeVisible();
    await expect(modelMenu).toHaveAttribute('role', 'menu');

    await page.screenshot({
      path: 'test-results/status-bar-model-menu-open.png',
      fullPage: false,
    });

    // Outside click closes (click the backdrop)
    await page.getByTestId('app-menu-backdrop').click({ force: true, position: { x: 10, y: 10 } });
    await expect(modelMenu).not.toBeVisible();
  });
});
