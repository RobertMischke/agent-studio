import { test, Page } from '@playwright/test';

/**
 * Zoom-in companion to the layout-review sweep — captures tighter
 * crops of the surfaces I want to evaluate for premium-style polish:
 * card chrome, lane headers, chat composer, sidebar tree, statusbar.
 * One-shot tool, screenshots-as-artifacts; not a regression spec.
 */
const SHOTS = 'playwright-screenshots/layout-zoom';

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => { try { localStorage.setItem('atp.studio.theme', t); } catch { /* noop */ } }, theme);
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(400);
}

async function clipShot(page: Page, name: string, clip: { x: number; y: number; width: number; height: number }): Promise<void> {
  await page.screenshot({ path: `${SHOTS}/${name}.png`, clip, fullPage: false });
}

test.describe('Layout zoom — premium polish review', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1000 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      try {
        localStorage.removeItem('atp.studio.orchestratorWidth');
        localStorage.removeItem('atp.studio.sidebarWidth');
      } catch { /* noop */ }
    });
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`captures zoom-ins in ${theme}`, async ({ page }) => {
      await setTheme(page, theme);

      // Open a project board (click first project in workspace tree).
      const firstProject = page.locator('.studio-sidebar app-tree-row button, .studio-tree-row--root').first();
      if (await firstProject.count() > 0) {
        await firstProject.click();
        await page.waitForTimeout(400);
      }

      // 01. Top-left chunk: titlebar + activity bar + sidebar header.
      await clipShot(page, `${theme}-01-top-left`, { x: 0, y: 0, width: 350, height: 200 });

      // 02. Workspace tree section (sidebar body).
      await clipShot(page, `${theme}-02-sidebar-tree`, { x: 48, y: 60, width: 260, height: 400 });

      // 03. A single lane (kanban column header + first few cards).
      await clipShot(page, `${theme}-03-lane-detail`, { x: 320, y: 60, width: 360, height: 600 });

      // 04. Statusbar — bottom strip.
      await clipShot(page, `${theme}-04-statusbar`, { x: 0, y: 970, width: 1600, height: 30 });

      // 05. Open orchestrator + capture composer area.
      const orchToggle = page.getByTestId('orch-side-sheet-toggle');
      if (await orchToggle.count() > 0) {
        await orchToggle.click();
        await page.waitForTimeout(450);
        await clipShot(page, `${theme}-05-orch-header`, { x: 960, y: 0, width: 640, height: 100 });
        await clipShot(page, `${theme}-06-orch-composer`, { x: 960, y: 700, width: 640, height: 250 });
      }
    });
  }
});
