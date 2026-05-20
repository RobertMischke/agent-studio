import { test, expect } from '@playwright/test';

/**
 * Regression for the studio-shell sidebar resize handle. Mirrors the
 * orchestrator side-sheet resize spec — the two splitters share the
 * same UX (drag a 6 px hit-zone, persist via localStorage). After the
 * narrow-viewport push-contract fix removed the inline `[style.width.px]`
 * on the `<aside class="studio-sidebar">`, the grid track became the
 * sole source of truth for sidebar width. This spec proves the resize
 * handle still drives the track (no broken visual feedback) and that
 * the user choice survives a reload.
 */
test.describe('Studio sidebar resize', () => {
  test('drag widens the sidebar and persists across reloads', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      try { localStorage.removeItem('atp.studio.sidebarWidth'); } catch { /* ignore */ }
    });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);

    const sidebar = page.getByTestId('studio-sidebar');
    await expect(sidebar).toBeVisible({ timeout: 10_000 });
    const widthBefore = (await sidebar.boundingBox())!.width;

    // Grab the resize handle (last child of the sidebar — class
    // .studio-sidebar__resize, role separator). The handle has no
    // data-testid yet; locate by role inside the sidebar.
    const handle = sidebar.locator('.studio-sidebar__resize');
    await expect(handle).toHaveCount(1);
    const handleBox = (await handle.boundingBox())!;

    // Drag 120 px to the RIGHT (sidebar is on the viewport's left edge,
    // so dragging the handle right widens it).
    const startX = handleBox.x + handleBox.width / 2;
    const startY = handleBox.y + handleBox.height / 2;
    await page.mouse.move(startX, startY);
    await page.mouse.down();
    await page.mouse.move(startX + 120, startY, { steps: 8 });
    await page.mouse.up();
    await page.waitForTimeout(150);

    const widthAfter = (await sidebar.boundingBox())!.width;
    expect(widthAfter - widthBefore).toBeGreaterThan(80);
    expect(widthAfter - widthBefore).toBeLessThan(160);

    const persisted = await page.evaluate(() => localStorage.getItem('atp.studio.sidebarWidth'));
    expect(persisted).not.toBeNull();
    expect(parseInt(persisted!, 10)).toBeGreaterThan(widthBefore + 80);

    // Reload — width must come back from localStorage.
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);
    await expect(page.getByTestId('studio-sidebar')).toBeVisible();
    const widthAfterReload = (await page.getByTestId('studio-sidebar').boundingBox())!.width;
    expect(Math.abs(widthAfterReload - widthAfter)).toBeLessThan(4);
  });
});
