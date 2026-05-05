import { test } from '@playwright/test';
import * as path from 'path';

/**
 * Static screenshot capture for the docs/mockups/vscode-layout/ui.html spec.
 * Not part of the regression suite — opt in via the @mockup tag. Runs only
 * when explicitly invoked. Output lands in test-results/ and is copied into
 * docs/mockups/vscode-layout/evidence/ by the task workflow.
 */
test.describe('@mockup vscode-layout', () => {
  test('captures the interactive mockup at 1440x900', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    const file = path.resolve(__dirname, '../../docs/mockups/vscode-layout/ui.html');
    await page.goto('file://' + file);
    await page.waitForLoadState('domcontentloaded');
    await page.screenshot({
      path: 'test-results/vscode-layout-mockup-task.png',
      fullPage: false,
    });

    // Switch to board view and capture.
    await page.locator('.titlebar__btn[data-view="board"]').click();
    await page.screenshot({
      path: 'test-results/vscode-layout-mockup-board.png',
      fullPage: false,
    });

    // Reopen task view, open Meta panel, capture.
    await page.locator('.titlebar__btn[data-view="task"]').click();
    await page.locator('#meta-toggle').click();
    await page.screenshot({
      path: 'test-results/vscode-layout-mockup-task-meta-open.png',
      fullPage: false,
    });
  });
});
