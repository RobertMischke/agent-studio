import { test } from '@playwright/test';

test('Mini test - take a screenshot', async ({ page }) => {
  // Visit the application
  await page.goto('http://localhost:4200', { waitUntil: 'networkidle', timeout: 10000 }).catch(() => {
    console.log('App not available, visiting example.com instead');
    return page.goto('https://example.com');
  });

  // Take a screenshot
  await page.screenshot({ path: 'mini-test-screenshot.png' });
  console.log('Screenshot saved to mini-test-screenshot.png');
});
