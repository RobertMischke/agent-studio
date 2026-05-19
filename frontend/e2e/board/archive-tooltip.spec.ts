import { test, expect } from '@playwright/test';

/**
 * Visual + behavioural coverage for the archive-row redesign:
 *   - The project name text is gone from the row body; only the colored
 *     initial disk remains. The task title gets the freed space.
 *   - The Archive column rows expose an instant tooltip that surfaces
 *     project / agent / created / last-activity info — no native title
 *     attribute, so no 1.5 s browser delay before the user sees it.
 */
async function dismissErrorOverlay(page: import('@playwright/test').Page) {
  const overlay = page.locator('.overlay--error');
  if (await overlay.isVisible({ timeout: 500 }).catch(() => false)) {
    await overlay.click({ force: true }).catch(() => {});
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

test.describe('Archive row layout & tooltip', () => {
  test('archive rows show only the initial disk (no project label) and emit an instant tooltip on hover', async ({ page }) => {
    await page.goto('/');
    await dismissErrorOverlay(page);
    await expect(page.getByRole('heading', { name: 'Archive', exact: true })).toBeVisible({ timeout: 10_000 });

    const firstRow = page.getByTestId('archive-row').first();
    await expect(firstRow).toBeVisible({ timeout: 10_000 });

    // The redesign drops `.archive-row__project` (label + uppercase project
    // name) and keeps only the disk; assert the label class is gone.
    expect(await firstRow.locator('.archive-row__project').count()).toBe(0);
    await expect(firstRow.locator('.archive-row__disk')).toBeVisible();
    await expect(firstRow.locator('.archive-row__title')).toBeVisible();

    // No native title attribute — the new directive is the only tooltip
    // surface, otherwise the browser would race and double-show.
    await expect(firstRow).not.toHaveAttribute('title', /.+/);

    // Hover should reveal the floating tooltip instantly (no 1.5 s wait).
    await firstRow.hover();
    const tip = page.getByTestId('instant-tooltip');
    await expect(tip).toBeVisible({ timeout: 500 });
    await expect(tip).toContainText('Project:');
    await expect(tip).toContainText('Created:');
    await expect(tip).toContainText('Last activity:');

    // Move the cursor away — tooltip retreats.
    await page.mouse.move(0, 0);
    await expect(tip).toBeHidden({ timeout: 500 });

    // Capture for the chat reply.
    await firstRow.hover();
    await page.screenshot({
      path: 'test-results/archive-row-tooltip.png',
      clip: await getColumnClip(page)
    });
  });

  test('"Archive all" button uses the instant tooltip (no native title)', async ({ page }) => {
    await page.goto('/');
    await dismissErrorOverlay(page);
    const btn = page.getByTestId('archive-all-btn');
    await expect(btn).toBeVisible({ timeout: 10_000 });
    await expect(btn).not.toHaveAttribute('title', /.+/);

    await btn.hover();
    const tip = page.getByTestId('instant-tooltip');
    await expect(tip).toBeVisible({ timeout: 500 });
    await expect(tip).toHaveText(/Move all completed tasks to Archive/i);
  });
});

async function getColumnClip(page: import('@playwright/test').Page) {
  const handle = await page.locator('.column--archive').first().elementHandle();
  if (!handle) return undefined;
  const box = await handle.boundingBox();
  if (!box) return undefined;
  return {
    x: Math.max(0, box.x - 8),
    y: Math.max(0, box.y - 8),
    width: Math.min(720, box.width + 16),
    height: Math.min(900, box.height + 16)
  };
}
