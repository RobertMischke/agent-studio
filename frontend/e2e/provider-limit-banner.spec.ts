import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme, type Theme } from './helpers/theme';

const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim() || 'test-results';
const THEMES: readonly Theme[] = ['light', 'dark'];

test('provider limit and automatic recovery stay visible in both themes', async ({ page }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  await page.route('**/api/runner/status', route => route.fulfill({
    json: {
      projects: {},
      providerLimits: [{
        provider: 'claude',
        observedAt: '2026-08-23T22:00:00Z',
        retryAt: '2026-08-24T00:20:00Z',
        reason: 'Claude account session limit reached; resets 12:20am.',
        reportedReset: '12:20am',
      }],
    },
  }));
  await page.route('**/api/auth/status', route => route.fulfill({
    json: { profile: 'local', bootstrapRequired: false, authenticated: true },
  }));
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');

  const banner = page.getByTestId('provider-limit-banner');
  await expect(banner).toBeVisible();
  await expect(banner).toContainText('claude: limited until');
  await expect(banner).toContainText('Waiting cards resume automatically');
  await expect(banner).toContainText('other CLIs remain eligible');

  for (const theme of THEMES) {
    await setTheme(page, theme);
    await banner.screenshot({
      path: `${RESULTS_DIR}/provider-limit-banner--${theme}--mocked.png`,
    });
  }
});
