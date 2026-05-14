import { test, expect } from '@playwright/test';

/**
 * Regression coverage for the right-side-sheet contract.
 *
 * The chat surface must:
 *  - open as a persistent right-side panel (not a centered modal)
 *  - keep the board / workspace visible behind it (no backdrop overlay)
 *  - sit on the right edge of the viewport
 *  - not be titled "Project window" (which previously implied it owned
 *    the project shell window)
 *
 * The 2026-05-11 crash-recovery commit accidentally promoted the chat
 * into a centered "Project window" modal that covered the board; this
 * spec locks the position so it cannot regress without a red test.
 */
const SHOTS = 'screenshots/orch-side-sheet-position';

test.describe('Orchestrator side sheet position', () => {
  test('opens as a right-side panel that leaves the board visible', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.mouse.move(0, 0);
    await page.waitForTimeout(500);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });

    const board = page.getByTestId('kanban-dashboard');
    await expect(board).toBeVisible();
    const boardBoxBefore = await board.boundingBox();
    expect(boardBoxBefore).not.toBeNull();

    await toggle.click();
    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();
    await page.waitForTimeout(400);

    await page.screenshot({ path: `${SHOTS}/01-open.png`, fullPage: false });

    // Title is no longer the misleading "Project window".
    await expect(page.getByTestId('orch-side-sheet-title')).toHaveText(/^(Chat|Orchestrator)/i);
    await expect(sheet).not.toContainText('Project window');

    // Sheet sits on the right edge of the viewport: its right edge is at
    // (or within a few px of) the viewport's right edge, and its left
    // edge is well past the viewport's left half. This is the load-
    // bearing assertion that distinguishes "right side sheet" from
    // "centered modal" (the previous regression had the panel margin-auto
    // centered with ~50px gutters on both sides).
    const viewport = page.viewportSize();
    expect(viewport).not.toBeNull();
    const sheetBox = await sheet.boundingBox();
    expect(sheetBox).not.toBeNull();
    expect(sheetBox!.x + sheetBox!.width).toBeGreaterThan(viewport!.width - 8);
    expect(sheetBox!.x).toBeGreaterThan(viewport!.width * 0.4);

    // Sheet is narrower than the viewport — no centered-modal mode with
    // 90+ % width. 80 % is the cutoff: the new layout caps at 640px,
    // which is well under 80 % of a 1280-wide viewport but allows for
    // narrower viewports to still pass.
    expect(sheetBox!.width).toBeLessThan(viewport!.width * 0.8);

    // Board is still mounted and at least partially visible — the sheet
    // does not paint a full-viewport backdrop over the workspace.
    const boardBoxAfter = await board.boundingBox();
    expect(boardBoxAfter).not.toBeNull();
    expect(boardBoxAfter!.width).toBeGreaterThan(120);

    // No backdrop element from the centered-modal era. The previous
    // implementation laid down a `position: fixed; inset: 0` overlay
    // before the sheet; we look for any element that fully covers the
    // viewport behind the sheet (other than the sheet host itself).
    const fullCoverElements = await page.evaluate(() => {
      const out: { tag: string; cls: string }[] = [];
      const vw = window.innerWidth;
      const vh = window.innerHeight;
      for (const el of Array.from(document.body.querySelectorAll('*'))) {
        const r = (el as HTMLElement).getBoundingClientRect();
        const cs = window.getComputedStyle(el);
        if (cs.position !== 'fixed') continue;
        if (r.width < vw - 4 || r.height < vh - 4) continue;
        if ((el as HTMLElement).closest('[data-testid="orch-side-sheet"]')) continue;
        out.push({ tag: el.tagName.toLowerCase(), cls: (el as HTMLElement).className || '' });
      }
      return out;
    });
    expect(fullCoverElements).toEqual([]);

    // Tabs are still reachable inside the side sheet.
    await expect(page.getByTestId('orch-side-sheet-tab-project')).toBeVisible();

    // Close and re-open to confirm the flex-collapse pattern (host width
    // returns to zero so the board reclaims its full width).
    await page.getByTestId('orch-side-sheet-close').click();
    await page.waitForTimeout(400);
    const boardBoxClosed = await board.boundingBox();
    expect(boardBoxClosed).not.toBeNull();
    expect(boardBoxClosed!.width).toBeGreaterThanOrEqual(boardBoxAfter!.width);

    await page.screenshot({ path: `${SHOTS}/02-closed.png`, fullPage: false });
  });
});
