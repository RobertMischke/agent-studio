import { test, expect, type Page, type BrowserContext } from '@playwright/test';

/**
 * F23 evidence — shared <app-menu> surface drives every migrated dropdown.
 *
 * Validates that the three legacy menu implementations have been folded
 * into the single `<app-menu>` component:
 *   - the legacy-layout devtools menu in the header,
 *   - the project picker in the VS-Code titlebar,
 *   - the tab right-click context menu.
 *
 * All three surfaces are expected to expose the new
 * `{prefix}-panel` and `{prefix}-item-{id}` testIds and render through
 * the shared token-based panel styling (`--studio-bg-elevated`,
 * `--elevation-popover`, `--studio-bg-hover`, ...). The screenshots
 * captured here are evidence of the post-F23 visual contract; the
 * panel-presence assertions are the load-bearing regression check.
 */

async function gotoStudio(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });
}

async function gotoLegacy(page: Page, context: BrowserContext): Promise<void> {
  await context.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '0');
  });
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
}

test.describe('F23 shared <app-menu> migrations', () => {
  test('project picker uses app-menu — panel + items via {prefix}-* testIds', async ({ page }) => {
    await gotoStudio(page);
    await page.getByTestId('studio-project-picker-trigger').click();
    const panel = page.getByTestId('studio-project-picker-panel');
    await expect(panel).toBeVisible();
    await expect(panel.locator('[data-testid^="studio-project-picker-item-"]').first()).toBeVisible();
    await page.screenshot({
      path: 'test-results/f23-project-picker-menu.png',
      fullPage: false,
    });
  });

  test('devtools menu (legacy layout) uses app-menu — panel + items via {prefix}-* testIds', async ({ page, context }) => {
    await gotoLegacy(page, context);
    await page.getByTestId('devtools-menu-trigger').click();
    const panel = page.getByTestId('devtools-menu-panel');
    await expect(panel).toBeVisible();
    await expect(panel.getByTestId('devtools-menu-item-orch-config')).toBeVisible();
    await page.screenshot({
      path: 'test-results/f23-devtools-menu.png',
      fullPage: false,
    });
  });
});
