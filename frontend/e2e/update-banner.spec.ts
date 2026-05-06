import { expect, test } from '@playwright/test';

/**
 * Update surface contract (phase 2): version badge in the header, an
 * Update Center drawer behind it, a full-screen block modal during a
 * run, and a small done/failed toast after.
 *
 * Skips when the standalone UpdateService is not reachable so a CI run
 * without the sibling process does not turn red.
 */
test.describe('update surface', () => {
  test.beforeEach(async ({ page, baseURL }) => {
    const url = new URL(baseURL ?? 'http://localhost:4011');
    const probe = await page.request.get(`${url.protocol}//${url.hostname}:5039/healthz`).catch(() => null);
    test.skip(!probe?.ok(), 'UpdateService not reachable on :5039.');
  });

  test('version badge renders the product version + short SHA', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);

    const badge = page.locator('[data-testid="version-badge"]').first();
    await expect(badge).toBeVisible();
    const text = await badge.innerText();
    // Expect at least "v" followed by something, plus a 7-char SHA.
    expect(text).toMatch(/^v[\d.]+/);
    expect(text).toMatch(/[0-9a-f]{7}/);
  });

  test('update center drawer opens and closes', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);

    await page.locator('[data-testid="version-badge"]').first().click();
    const center = page.locator('[data-testid="update-center"]');
    await expect(center).toBeVisible();

    await page.locator('[data-testid="update-center-close"]').click();
    await expect(center).not.toBeVisible();
  });

  test('manual trigger from update center surfaces the block modal', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);

    await page.locator('[data-testid="version-badge"]').first().click();
    await expect(page.locator('[data-testid="update-center"]')).toBeVisible();

    await page.locator('[data-testid="update-center-trigger"]').click();

    // Block modal must appear within ~5 s (FE switches to 2 s polling
    // immediately after the trigger() helper resolves).
    const block = page.locator('[data-testid="update-block-modal"]');
    await expect(block, 'block modal should appear within 5 s').toBeVisible({ timeout: 5_000 });
    const phase = await page.locator('[data-testid="update-block-phase"]').innerText();
    expect(phase.length).toBeGreaterThan(0);

    // Wait for the run to settle.
    await expect(block, 'block modal should clear when the run is done').not.toBeVisible({ timeout: 90_000 });
  });
});
