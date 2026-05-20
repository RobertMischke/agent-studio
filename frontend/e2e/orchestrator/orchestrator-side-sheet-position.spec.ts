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

  /**
   * Push contract (not overlay). Opening the orchestrator chat must
   * narrow the studio-shell by approximately the panel width; the inner
   * `<app-sidesheet>` chrome must fill the full open host width (no
   * transparent gap on the right); the panel host must stay in normal
   * flex flow (position: static).
   *
   * Three earlier regressions are pinned here:
   *  - The host was `position: fixed` via a studio-mode workaround in
   *    styles.scss, which forced overlay behaviour and prevented any
   *    push. Fixed by introducing the `.app-shell` flex parent and
   *    deleting the `position: fixed` rule.
   *  - The inner `.sidesheet` div had `width: 360px` hard-coded in
   *    `components/sidesheet/sidesheet.component.scss`, leaving a
   *    transparent 280 px gap inside the open 640 px host.
   *  - The studio-shell had to be ordered to the *left* of the side
   *    sheets without reshuffling the DOM, which `.app-shell` does via
   *    `flex-direction: row-reverse`.
   */
  test('open pushes studio-shell + inner panel fills host', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);

    const studio = page.locator('app-studio-shell');
    const widthClosed = (await studio.boundingBox())!.width;

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();
    await page.getByTestId('orch-side-sheet').waitFor({ state: 'visible' });
    await page.waitForTimeout(450);

    const widthOpen = (await studio.boundingBox())!.width;

    const geometry = await page.evaluate(() => {
      const orchHost = document.querySelector('app-orchestrator-side-sheet') as HTMLElement | null;
      const inner = document.querySelector('[data-testid="orch-side-sheet"]') as HTMLElement | null;
      const studio = document.querySelector('app-studio-shell') as HTMLElement | null;
      function box(el: HTMLElement | null) {
        if (!el) return null;
        const r = el.getBoundingClientRect();
        const cs = window.getComputedStyle(el);
        return {
          width: Math.round(r.width),
          x: Math.round(r.x),
          right: Math.round(r.x + r.width),
          position: cs.position,
        };
      }
      return { orchHost: box(orchHost), inner: box(inner), studio: box(studio), vw: window.innerWidth };
    });

    // Inner sidesheet fills its open host (no gap on the right).
    expect(geometry.inner!.width).toBe(geometry.orchHost!.width);

    // Push, not overlay: studio-shell narrows by approximately the panel
    // width on open, and grows back on close.
    expect(widthClosed - widthOpen).toBeGreaterThan(400);

    // Studio-shell sits to the LEFT of the orchestrator (i.e. the panel
    // is on the right edge). Their boxes are non-overlapping along x.
    expect(geometry.studio!.right).toBeLessThanOrEqual(geometry.orchHost!.x + 1);
    expect(geometry.orchHost!.right).toBeGreaterThanOrEqual(geometry.vw - 4);

    // Host stays in normal flow (not `position: fixed`, which was the
    // overlay regression). The body's `.app-shell` flex parent does the
    // push; nothing should pull the host out of flow.
    expect(geometry.orchHost!.position).toBe('static');
  });

  /**
   * Compact-viewport push contract. At 1280 × 720 the orchestrator sheet
   * (640 px) leaves ~640 px for the studio-shell, which is less than the
   * sum of the activity bar (48) + the default user-chosen sidebar
   * (240 px) + editor min-content. The earlier layout pinned the sidebar
   * to its user width via inline `[style.width.px]` and used a non-
   * shrinkable grid track, so the studio-shell intrinsic width grew past
   * its container box and `.app-shell { overflow: hidden }` clipped the
   * right edge of the editor — visually it looked like the orchestrator
   * was painting over the editor.
   *
   * The fix: grid uses `minmax(0, sidebarWidth)px` for the sidebar track
   * and `min-width: 0` on `.studio-sidebar`, so the sidebar shrinks under
   * pressure while the user-chosen width remains the *cap*. This test
   * pins it: the editor's right edge stays inside the studio-shell box
   * when the sheet opens on a 1280 px viewport.
   */
  test('push contract holds on a 1280 px viewport (no editor clip)', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 720 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();
    await page.getByTestId('orch-side-sheet').waitFor({ state: 'visible' });
    await page.waitForTimeout(450);

    const geometry = await page.evaluate(() => {
      function box(el: Element | null) {
        if (!el) return null;
        const r = (el as HTMLElement).getBoundingClientRect();
        return { x: Math.round(r.x), width: Math.round(r.width), right: Math.round(r.x + r.width) };
      }
      return {
        studio: box(document.querySelector('app-studio-shell')),
        editor: box(document.querySelector('app-studio-shell .studio-editor')),
        orchHost: box(document.querySelector('app-orchestrator-side-sheet')),
        vw: window.innerWidth,
      };
    });

    // The orchestrator sits on the right edge.
    expect(geometry.orchHost!.right).toBeGreaterThanOrEqual(geometry.vw - 4);
    // The studio-shell ends where the orchestrator begins — no overlap.
    expect(geometry.studio!.right).toBeLessThanOrEqual(geometry.orchHost!.x + 1);
    // The editor stays inside the studio-shell box (this is what fails
    // when the sidebar refuses to shrink and pushes the editor right).
    expect(geometry.editor!.right).toBeLessThanOrEqual(geometry.studio!.right + 1);
  });
});
