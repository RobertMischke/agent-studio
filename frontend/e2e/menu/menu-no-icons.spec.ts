import { test, expect } from '@playwright/test';

/**
 * Regression cover for the "Menu surfaces are text-only" convention
 * (see AGENTS.md). Any `<app-menu>` panel that opens in the app must
 * not contain decorative leading icons. The `leadingGlyph` chip used
 * by the project picker is allowed; raw `<img>` / `<svg>` /
 * `.app-menu__icon` spans are not.
 *
 * The spec opens two representative panels:
 *   1. The studio-shell tab right-click context menu (`copy-name`,
 *      `copy-id`, `copy-key`, `close*` rows). This is the menu whose
 *      previous emoji icons triggered the convention.
 *   2. The studio-shell project picker (`leadingGlyph` is allowed —
 *      we assert the panel renders without falling back to icon
 *      spans, but glyph chips remain present).
 *
 * Asserting on `.app-menu__icon` directly is the load-bearing check:
 * the class is removed from the template, so any future
 * `<app-menu-item icon="…">`-style reintroduction would re-add it and
 * break this spec immediately.
 */
test.describe('Menu surfaces are text-only', () => {
  async function bootStudio(page: import('@playwright/test').Page): Promise<void> {
    await page.setViewportSize({ width: 1400, height: 900 });
    await page.goto('/');
    await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });
  }

  test('tab right-click context menu contains no icons', async ({ page }) => {
    await bootStudio(page);

    // Open at least one project Hub tab so a tab exists to right-click.
    const projectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
    if (await projectRow.count() === 0) {
      test.skip(true, 'No projects loaded — tab context menu spec needs at least one project.');
      return;
    }
    await projectRow.locator('.studio-tree-row__hub-link').first().click();

    const tab = page.locator('.studio-tab').first();
    await expect(tab).toBeVisible({ timeout: 5_000 });
    await tab.click({ button: 'right' });

    const panel = page.getByTestId('studio-tab-ctx-panel');
    await expect(panel).toBeVisible({ timeout: 3_000 });

    // The Close row is always present in this menu.
    await expect(panel.getByTestId('studio-tab-ctx-item-close')).toBeVisible();

    // No raw <img>, no <svg>, no leftover .app-menu__icon spans in the rows.
    await expect(panel.locator('.app-menu__icon')).toHaveCount(0);
    await expect(panel.locator('img')).toHaveCount(0);
    await expect(panel.locator('svg')).toHaveCount(0);

    // Each focusable menu row's first non-text child (if any) must be a
    // leadingGlyph chip, never an icon span. Tab ctx menu has no glyphs.
    const items = panel.locator('[role="menuitem"]');
    const count = await items.count();
    for (let i = 0; i < count; i++) {
      const row = items.nth(i);
      await expect(row.locator('.app-menu__icon')).toHaveCount(0);
    }

    await page.screenshot({
      path: 'test-results/menu-no-icons-tab-ctx.png',
      fullPage: false,
    });
  });

  test('project picker leadingGlyph chips render without icon spans', async ({ page }) => {
    await bootStudio(page);

    const trigger = page.getByTestId('studio-project-picker-trigger');
    if (await trigger.count() === 0) {
      test.skip(true, 'No project picker on this layout build.');
      return;
    }
    await trigger.click();
    const panel = page.getByTestId('studio-project-picker-panel');
    await expect(panel).toBeVisible({ timeout: 3_000 });

    // Project rows DO carry coloured initial chips — that's `leadingGlyph`,
    // explicitly allowed by the convention. But no `.app-menu__icon`.
    await expect(panel.locator('.app-menu__glyph').first()).toBeVisible();
    await expect(panel.locator('.app-menu__icon')).toHaveCount(0);
    await expect(panel.locator('img')).toHaveCount(0);
    await expect(panel.locator('svg')).toHaveCount(0);

    await page.screenshot({
      path: 'test-results/menu-no-icons-project-picker.png',
      fullPage: false,
    });
  });
});
