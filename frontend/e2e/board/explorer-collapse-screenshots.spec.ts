import { test } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * F27 visual evidence: capture the Explorer panel in three states so a
 * reviewer can confirm the chevrons, "Show all projects" inline button,
 * and project-row label toggle behave as designed.
 *
 * Output goes to the job folder when JOB_RESULTS_DIR is set, otherwise
 * to `test-results/` next to other Playwright artefacts.
 */
const dest = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.join(__dirname, '..', '..', 'test-results', 'f27-screenshots');

test.describe('F27 visual evidence', () => {
  test.beforeAll(async () => {
    fs.mkdirSync(dest, { recursive: true });
  });

  test('explorer panel — workspace expanded, default state', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
      try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
    });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);
    const sidebar = page.getByTestId('studio-sidebar');
    await sidebar.screenshot({ path: path.join(dest, '01-workspace-expanded.png') });
  });

  test('explorer panel — workspace collapsed', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
    });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);
    await page.getByTestId('studio-explorer-workspace-head').click();
    await page.waitForTimeout(200);
    const sidebar = page.getByTestId('studio-sidebar');
    await sidebar.screenshot({ path: path.join(dest, '02-workspace-collapsed.png') });
  });

  test('explorer panel — project label-click toggles row', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
      try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
    });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);
    const firstRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
    if ((await firstRow.count()) === 0) return;
    // Make sure the row is expanded so the screenshot shows lane children.
    const label = firstRow.locator('button.tree-row').first();
    const children = firstRow.locator('.studio-tree-children');
    if ((await children.count()) === 0) {
      await label.click();
      await page.waitForTimeout(150);
    }
    const sidebar = page.getByTestId('studio-sidebar');
    await sidebar.screenshot({ path: path.join(dest, '03-project-row-expanded.png') });

    // Collapse via label-click.
    await label.click();
    await page.waitForTimeout(150);
    await sidebar.screenshot({ path: path.join(dest, '04-project-row-collapsed.png') });
  });
});
