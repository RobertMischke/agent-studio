import { test, expect } from '@playwright/test';

/**
 * The dev checkout marks itself visually so it can never be confused with
 * the stable checkout. Activation is driven by GET /api/environment, which
 * the backend derives from a gitignored appsettings.Local.json. With the
 * flag on the page must:
 *   - render a fixed orange "DEV" banner (data-testid="dev-banner")
 *   - swap the SVG favicon to icons-dev/icon.svg
 *   - point the manifest link at manifest-dev.webmanifest
 *   - set the document title to "Agent Task Processor (DEV)"
 */
test.describe('DEV-mode visual markers', () => {
  test.beforeEach(async ({ request }) => {
    const res = await request.get('/api/environment');
    expect(res.ok(), '/api/environment must respond').toBeTruthy();
    const body = await res.json();
    test.skip(body.isDev !== true, 'DEV flag is off — banner cannot render');
  });

  test('shows DEV banner, dev manifest, dev favicon, dev title', async ({ page }) => {
    await page.goto('/');

    const banner = page.getByTestId('dev-banner');
    await expect(banner).toBeVisible();
    await expect(banner).toHaveText(/DEV/);

    await expect(page).toHaveTitle('Agent Task Processor (DEV)');

    const manifestHref = await page.locator('link[rel="manifest"]').getAttribute('href');
    expect(manifestHref).toBe('manifest-dev.webmanifest');

    const svgFavicon = await page
      .locator('link[rel="icon"][type="image/svg+xml"]')
      .getAttribute('href');
    expect(svgFavicon).toBe('icons-dev/icon.svg');

    await page.screenshot({ path: 'test-results/dev-banner.png', fullPage: false });
  });
});
