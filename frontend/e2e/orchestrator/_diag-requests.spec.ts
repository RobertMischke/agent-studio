import { test } from '@playwright/test';

// Throwaway diagnostic: enumerate every /api request (and websocket) the board
// fires on load so we can stub them hermetically. Deleted after use.
test('diag: list api requests on board load', async ({ page }) => {
  const seen: string[] = [];
  page.on('request', (req) => {
    const u = req.url();
    if (u.includes('/api/') || u.includes('/jobs') || u.includes('negotiate') || u.startsWith('ws')) {
      seen.push(`${req.method()} ${u.replace('http://localhost:4010', '')}`);
    }
  });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(4000);
  console.log('=== API/WS REQUESTS ===');
  for (const line of Array.from(new Set(seen)).sort()) console.log(line);
  console.log('=== END ===');
});
