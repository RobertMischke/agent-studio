import { test, expect } from '@playwright/test';

/**
 * Regression: commit-pill tooltip lists files inside a fixed-width box.
 * Long paths used to spill out past the rounded border. The fix installs
 * overflow/ellipsis on `<li>` rows; this spec proves no list row's
 * right edge exceeds the tooltip box's right edge.
 */
test('commit-pill tooltip clips long file rows inside the box', async ({ page }) => {
  await page.goto('/');

  const overlay = page.locator('.overlay--error');
  if (await overlay.isVisible({ timeout: 500 }).catch(() => false)) {
    await overlay.click({ force: true }).catch(() => {});
  }
  // Strip any stale Vite error overlay that intercepts pointer events.
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach(n => n.remove());
  });

  // Pick the first commit row whose tooltip will carry long file paths.
  // We probe several visible rows to find one with a populated file list.
  const pills = page.getByTestId('task-card-commit-row');
  await expect(pills.first()).toBeVisible({ timeout: 15_000 });
  const count = await pills.count();

  let chosen: ReturnType<typeof page.getByTestId> | null = null;
  for (let i = 0; i < Math.min(count, 60); i++) {
    const pill = pills.nth(i);
    const hasFiles = await pill.getAttribute('data-has-files');
    if (hasFiles === 'true') {
      chosen = pill;
      break;
    }
  }
  expect(chosen, 'no commit pill exposes a file list — seed data has none').not.toBeNull();

  await chosen!.scrollIntoViewIfNeeded();
  await chosen!.hover();

  const tip = page.getByTestId('app-tooltip');
  await expect(tip).toBeVisible({ timeout: 1_000 });

  // The tooltip must actually contain a <ul> file list to exercise the fix.
  await expect(tip.locator('ul li').first()).toBeVisible();

  const tipBox = await tip.boundingBox();
  expect(tipBox).not.toBeNull();
  const items = tip.locator('ul li');
  const itemCount = await items.count();
  for (let i = 0; i < itemCount; i++) {
    const itemBox = await items.nth(i).boundingBox();
    if (!itemBox) continue;
    // The list item's right edge must stay inside the tooltip's right edge.
    expect(itemBox.x + itemBox.width).toBeLessThanOrEqual(tipBox!.x + tipBox!.width + 0.5);
  }

  const padded = {
    x: Math.max(0, tipBox!.x - 24),
    y: Math.max(0, tipBox!.y - 24),
    width: Math.min(1200, tipBox!.width + 48),
    height: Math.min(900, tipBox!.height + 48)
  };
  await page.screenshot({
    path: 'test-results/commit-tooltip-clipped.png',
    clip: padded
  });
});
