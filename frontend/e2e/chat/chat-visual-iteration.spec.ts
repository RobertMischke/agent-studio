import { test, expect, Page } from '@playwright/test';

/**
 * Visual-iteration script for the chat revamp (Phase 3 of the chat
 * revamp work). This is NOT a regression spec — it's a tool that
 * captures the chat surfaces in dark + light theme so the engineer
 * can review the result of the SCSS / template changes without
 * having to drive a browser by hand. Outputs go to
 * `playwright-screenshots/chat-revamp/<step>.png` and are reviewed
 * inline in the iteration loop.
 *
 * The screenshots cover, in order:
 *   1. Studio shell with the orchestrator side sheet closed
 *   2. Orchestrator side sheet open — default width
 *   3. Orchestrator side sheet open — wider via resize splitter
 *   4. Phase summary in compact (collapsed) state
 *   5. Phase summary expanded
 *   6. Same sequence in light theme
 */
const SHOTS = 'playwright-screenshots/chat-revamp';

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.setAttribute('data-studio-theme', t);
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
  // Give Angular signals + transitions a tick to settle.
  await page.waitForTimeout(250);
}

async function openOrchestrator(page: Page): Promise<void> {
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  // Only click if not already open — clicking a toggle when it's
  // already-open would close it.
  const opened = await page.locator('app-orchestrator-side-sheet.is-open').count();
  if (opened === 0) {
    await toggle.click();
    await page.getByTestId('orch-side-sheet').waitFor({ state: 'visible' });
    await page.waitForTimeout(400);
  }
}

async function closeOrchestrator(page: Page): Promise<void> {
  const opened = await page.locator('app-orchestrator-side-sheet.is-open').count();
  if (opened > 0) {
    // The orchestrator wraps `<app-sidesheet>`, whose close button
    // ships with `data-testid="sidesheet-close"`. Scope to the host
    // so a hidden kanban-filter-sidesheet sibling doesn't claim the click.
    await page.locator('app-orchestrator-side-sheet [data-testid="sidesheet-close"]').click();
    await page.waitForTimeout(300);
  }
}

test.describe('Chat revamp — visual iteration', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    // Start each pass from a clean width so the splitter-drag step
    // produces consistent output.
    await page.evaluate(() => {
      try { localStorage.removeItem('atp.studio.orchestratorWidth'); } catch { /* ignore */ }
    });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`captures chat surfaces in ${theme} theme`, async ({ page }) => {
      await setTheme(page, theme);

      // 1. Shell with orchestrator closed.
      await page.screenshot({ path: `${SHOTS}/${theme}-01-shell-closed.png`, fullPage: false });

      // 2. Orchestrator open — default width.
      await openOrchestrator(page);
      await page.screenshot({ path: `${SHOTS}/${theme}-02-orch-default.png`, fullPage: false });

      // 3. Resize splitter — drag the panel wider.
      const handle = page.getByTestId('orch-side-sheet-resize');
      if (await handle.count() > 0) {
        const box = (await handle.boundingBox())!;
        const startX = box.x + box.width / 2;
        const startY = box.y + box.height / 2;
        await page.mouse.move(startX, startY);
        await page.mouse.down();
        await page.mouse.move(startX - 220, startY, { steps: 12 });
        await page.mouse.up();
        await page.waitForTimeout(200);
        await page.screenshot({ path: `${SHOTS}/${theme}-03-orch-resized.png`, fullPage: false });
      }

      // 4. Phase summary state. If the phase summary list rendered,
      //    capture its compact + expanded form. Some sessions have no
      //    phases — guard the click so the test stays useful.
      const summary = page.locator('[data-testid="phase-summary-list"]');
      if (await summary.count() > 0) {
        await page.screenshot({ path: `${SHOTS}/${theme}-04-phase-compact.png`, fullPage: false });
        const compactToggle = page.locator('[data-testid="phase-summary-compact-toggle"]');
        if (await compactToggle.count() > 0) {
          await compactToggle.first().click();
          await page.waitForTimeout(200);
          await page.screenshot({ path: `${SHOTS}/${theme}-05-phase-expanded.png`, fullPage: false });
        }
      }

      // 6. Close + final shell shot to verify post-close geometry.
      await closeOrchestrator(page);
      await page.screenshot({ path: `${SHOTS}/${theme}-06-after-close.png`, fullPage: false });
    });
  }
});
