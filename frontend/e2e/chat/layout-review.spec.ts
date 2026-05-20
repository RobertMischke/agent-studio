import { test, expect, Page } from '@playwright/test';

/**
 * Layout review sweep — drives the app through its primary surfaces in
 * dark + light theme so the engineer can review the visual result of
 * the chat / shell revamp work without driving a browser by hand.
 *
 * NOT a regression spec — the screenshots are the artifact. Reviewed
 * by hand against the Catppuccin / 13px / 22px statusbar / 240px
 * sidebar targets pinned in docs/mockups/vscode-layout/.
 *
 * Surfaces captured:
 *   01  Studio shell — empty welcome
 *   02  Studio shell — project board open (kanban)
 *   03  Studio shell — project detail view
 *   04  Studio shell — job detail (when at least one task exists)
 *   05  Studio shell — orchestrator side sheet open (chat panel)
 *   06  Studio shell — orchestrator open + wide via splitter
 *   07  Studio shell — kanban filter sidesheet open
 *   08  Studio shell — sidebar collapsed (project hub view)
 */
const SHOTS = 'playwright-screenshots/layout-review';

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(400);
}

async function takeShot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

async function openProject(page: Page, projectIdx = 0): Promise<boolean> {
  // The studio sidebar workspace tree lists projects as collapsible
  // tree-rows. Click the first one's name link to open its board tab.
  const projects = page.locator('[data-testid^="studio-sidebar"]').locator('button:has-text("...")').first();
  // Fall-back: just click on any row that looks like a project.
  const treeRows = page.locator('.studio-tree-row--root, .studio-sidebar app-tree-row button').first();
  const found = await treeRows.count();
  if (found === 0) return false;
  await treeRows.click();
  await page.waitForTimeout(300);
  return true;
}

test.describe('Layout review — sweep', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`captures full set in ${theme} theme`, async ({ page }) => {
      await page.setViewportSize({ width: 1600, height: 1000 });
      // Reset local prefs so the sweep starts from a known shape.
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await page.evaluate(() => {
        try {
          localStorage.removeItem('atp.studio.orchestratorWidth');
          localStorage.removeItem('atp.studio.sidebarWidth');
        } catch { /* ignore */ }
      });
      await setTheme(page, theme);

      // 01. Welcome / empty shell.
      await takeShot(page, `${theme}-01-welcome`);

      // 02. Click the first project in the sidebar to open its board.
      await openProject(page);
      await takeShot(page, `${theme}-02-board`);

      // 05. Open the orchestrator side sheet.
      const orchToggle = page.getByTestId('orch-side-sheet-toggle');
      if (await orchToggle.count() > 0) {
        await orchToggle.click();
        await page.waitForTimeout(450);
        await takeShot(page, `${theme}-05-orch-chat`);

        // 06. Drag the splitter wider.
        const handle = page.getByTestId('orch-side-sheet-resize');
        if (await handle.count() > 0) {
          const hb = (await handle.boundingBox())!;
          await page.mouse.move(hb.x + hb.width / 2, hb.y + hb.height / 2);
          await page.mouse.down();
          await page.mouse.move(hb.x - 200, hb.y + hb.height / 2, { steps: 12 });
          await page.mouse.up();
          await page.waitForTimeout(250);
          await takeShot(page, `${theme}-06-orch-wide`);
        }

        // Close orchestrator.
        const close = page.locator('app-orchestrator-side-sheet [data-testid="sidesheet-close"]');
        if (await close.count() > 0) {
          await close.click();
          await page.waitForTimeout(300);
        }
      }

      // 07. Kanban filter sidesheet.
      const filterToggle = page.locator('[data-testid*="filter"]').first();
      if (await filterToggle.count() > 0) {
        try {
          await filterToggle.click({ timeout: 2000 });
          await page.waitForTimeout(350);
          await takeShot(page, `${theme}-07-filter`);
        } catch {
          // Filter sidesheet may not be reachable from current view; OK to skip.
        }
      }

      // 08. Click the activity-bar Explorer button to toggle sidebar
      //     (VS-Code-style collapse).
      const explorerBtn = page.locator('app-studio-activity-bar button').first();
      if (await explorerBtn.count() > 0) {
        await explorerBtn.click();
        await page.waitForTimeout(300);
        await takeShot(page, `${theme}-08-sidebar-collapsed`);
        // Restore.
        await explorerBtn.click();
        await page.waitForTimeout(300);
      }
    });
  }
});
