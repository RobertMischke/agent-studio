import { test } from '@playwright/test';

/**
 * Captures screenshots of the three locations affected by the CLI-icon work
 * so the user can eyeball the rendering. Not a regression check — assertions
 * live in cli-icons.spec.ts.
 */

test.describe('CLI icons — screenshots @screenshots', () => {
  test('capture quota strip, command deck, add-task dialog, and job board', async ({ page }) => {
    page.setViewportSize({ width: 1400, height: 900 });

    // 1) Quota strip in CLI Usage sidesheet (cost overview)
    await page.goto('/');
    await page.getByRole('button', { name: /usage|cli sessions/i }).first().click();
    await page.locator('aside.sheet').waitFor();
    // Wait briefly so quota cards have a chance to populate.
    await page.waitForTimeout(800);
    await page.locator('aside.sheet').screenshot({ path: 'test-results/screenshots/quota-strip.png' });
    await page.locator('aside.sheet button.sheet__close').click();

    // 2) Job board with preview cards
    await page.screenshot({ path: 'test-results/screenshots/board.png', fullPage: false });

    // 3) Add Task dialog (CLI picker is now the unified chip)
    await page.getByRole('button', { name: /add task/i }).first().click();
    await page.getByTestId('create-agent').waitFor();
    await page.locator('.create-dialog').screenshot({ path: 'test-results/screenshots/add-task-dialog.png' });
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);

    // 4) Command Deck (job-detail toolbar)
    const firstCard = page.locator('[data-testid="job-card"]').first();
    if (await firstCard.count() > 0) {
      await firstCard.click();
      const bar = page.locator('[data-testid="commandbar"]');
      await bar.waitFor();
      await bar.screenshot({ path: 'test-results/screenshots/command-deck.png' });
    }
  });
});
