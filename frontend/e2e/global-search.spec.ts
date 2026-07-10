import { test, expect } from './fixtures/dev-backend';

test('global palette groups results and supports keyboard navigation in both themes', async ({ page, devBackend: _devBackend }) => {
  await page.route('**/api/search?**', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ query: 'README', tasks: [], commits: [], errors: {}, durationMs: 7, files: [{
      domain: 'files', projectName: 'Agent Studio', projectColor: '#569cd6', title: 'README.md',
      subtitle: 'README.md', path: 'README.md', isWiki: false,
    }] }),
  }));

  await page.addInitScript(() => localStorage.setItem('atp.studio.theme', 'light'));
  await page.goto('/');
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused();
  await input.fill('README');
  await expect(page.getByTestId('global-search-group-files')).toContainText('README.md');
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[role="option"][aria-selected="true"]')).toHaveCount(1);

  const screenshotPath = process.env.GLOBAL_SEARCH_SCREENSHOT;
  if (screenshotPath) await page.screenshot({ path: screenshotPath, fullPage: true });

  await page.keyboard.press('Escape');
  await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
  await page.reload();
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await expect(page.getByTestId('global-search-input')).toBeVisible();
});
