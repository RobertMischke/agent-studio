import { test, expect, Page } from '@playwright/test';

/**
 * Locks in the "compact cards" header toggle. The control flips the board
 * cards between the default (full metadata: badges, agent line, model,
 * git pill, activity) and a dense one-row layout that hides everything
 * except the title plus a small CLI icon and a relative timestamp.
 * Persisted in localStorage as `compactCards=1` / `0`.
 *
 * The spec runs against whatever board state the configured backend
 * exposes; assertions that depend on cards being on the board are
 * gated so the spec stays useful on an empty board (only the toggle
 * mechanics + persistence are checked in that case).
 */

async function gotoBoard(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
  // Dismiss any transient error overlay that might cover the board.
  // The overlay's close.emit() also fires on backdrop-click, which is
  // more reliable than chasing the absolute-positioned close button.
  for (let i = 0; i < 3; i++) {
    const overlay = page.locator('.overlay--error');
    if ((await overlay.count()) === 0) break;
    if (!(await overlay.first().isVisible({ timeout: 200 }).catch(() => false))) break;
    await page.locator('.error-dialog__close').first().click({ timeout: 1_000 }).catch(() => {});
    await page.waitForTimeout(150);
  }
}

test.describe('Compact cards toggle', () => {
  test.beforeEach(async ({ page }) => {
    // Reset persisted preference so each test starts at the documented
    // default (full cards). One-shot — addInitScript would re-fire on
    // page.reload() and break the persistence assertion.
    await page.goto('/');
    await page.evaluate(() => { try { localStorage.removeItem('compactCards'); } catch { /* ignore */ } });
  });

  test('toggle button is in the header and round-trips between Full and Compact', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const toggle = page.getByTestId('compact-cards-toggle');
    await expect(toggle).toBeVisible();
    // Default: aria-pressed=false, label says "Full".
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await expect(toggle).toContainText('Full');

    await toggle.click();

    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await expect(toggle).toContainText('Compact');
    expect(await page.evaluate(() => localStorage.getItem('compactCards'))).toBe('1');

    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await expect(toggle).toContainText('Full');
    expect(await page.evaluate(() => localStorage.getItem('compactCards'))).toBe('0');
  });

  test('preference persists across reload', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const toggle = page.getByTestId('compact-cards-toggle');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');

    await page.reload();
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    const reloaded = page.getByTestId('compact-cards-toggle');
    await expect(reloaded).toHaveAttribute('aria-pressed', 'true');
    await expect(reloaded).toContainText('Compact');
  });

  test('cards on the board flip to data-compact and shrink in compact mode', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const cards = page.locator('[data-testid="job-card"]');
    // Give the first poll a moment to land cards in the DOM before we
    // decide whether to skip.
    await cards.first().waitFor({ state: 'attached', timeout: 8_000 }).catch(() => {});
    const count = await cards.count();
    if (count === 0) {
      test.skip(true, 'no jobs available on this board to verify card-level rendering');
      return;
    }

    // Default render: cards have NO data-compact flag.
    for (let i = 0; i < Math.min(count, 5); i++) {
      await expect(cards.nth(i)).not.toHaveAttribute('data-compact', 'true');
    }
    const fullHeight = await cards.first().evaluate((el) => Math.round((el as HTMLElement).getBoundingClientRect().height));
    expect(fullHeight).toBeGreaterThan(60);
    await page.screenshot({ path: 'test-results/compact-cards-toggle-full.png', fullPage: false });

    await page.getByTestId('compact-cards-toggle').click();

    // After toggle: every visible card carries the data-compact flag.
    for (let i = 0; i < Math.min(count, 5); i++) {
      await expect(cards.nth(i)).toHaveAttribute('data-compact', 'true');
    }
    // Title text is still rendered.
    await expect(cards.first().locator('.job-card__title-text')).toBeVisible();
    // Compact-only relative-time badge is rendered.
    await expect(cards.first().locator('.job-card__compact-time')).toBeVisible();
    // Model badge in compact mode is suppressed (display: none on the
    // host class). offsetParent === null is a robust visibility check.
    const modelHidden = await cards.first().locator('.job-card__model').first().evaluate(
      (el) => (el as HTMLElement).offsetParent === null
    ).catch(() => true);
    expect(modelHidden).toBe(true);

    const compactHeight = await cards.first().evaluate((el) => Math.round((el as HTMLElement).getBoundingClientRect().height));
    // The compact row must be meaningfully shorter so many more cards
    // fit on screen. Half the full height is a comfortable margin.
    expect(compactHeight).toBeLessThan(fullHeight / 2);
    await page.screenshot({ path: 'test-results/compact-cards-toggle-compact.png', fullPage: false });
  });
});
