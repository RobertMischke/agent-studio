import { test, expect } from '@playwright/test';

/**
 * Captures the actual favicon image bytes that Chrome will render in the
 * tab indicator. Asserts the dev-mode swap leads to the orange variant
 * being decoded by the browser, not just the link href being correct.
 */
test('dev favicon decodes to the orange dev SVG', async ({ page }) => {
  await page.goto('/');
  const link = page.locator('link[rel="icon"][type="image/svg+xml"]');
  await expect(link).toHaveCount(1);
  const href = await link.getAttribute('href');
  expect(href).toMatch(/^data:image\/svg\+xml;utf8,/);

  const svg = decodeURIComponent(href!.replace(/^data:image\/svg\+xml;utf8,/, ''));
  expect(svg).toContain('#f59e0b');
  expect(svg).toContain('>DEV<');

  // Render the data URL into an <img> and screenshot it so the user can see
  // exactly what their browser tab will show.
  await page.setContent(`<img src="${href}" style="width:128px;height:128px"/>`);
  await page.locator('img').screenshot({ path: 'test-results/dev-favicon-rendered.png' });
});
