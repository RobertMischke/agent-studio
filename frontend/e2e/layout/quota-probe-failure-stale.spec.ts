import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme, type Theme } from '../helpers/theme';

const SHOT_DIR = process.env.QUOTA_FAILURE_SHOT_DIR?.trim() || 'test-results';
const THEMES: readonly Theme[] = ['dark', 'light'];

test.describe('Quota probe failure degradation', () => {
  test('keeps last-good values with an attributable stale marker', async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    const failedAt = new Date(2026, 7, 23, 21, 7).toISOString();
    await page.route('**/api/auth/status', route => route.fulfill({
      json: { profile: 'local', bootstrapRequired: false, authenticated: true, user: null },
    }));
    await page.route('**/api/crash-recovery/pending', route => route.fulfill({ json: { pending: [] } }));
    await page.route('**/api/cli/quota', route => route.fulfill({
      json: {
        at: failedAt,
        ttlSeconds: 600,
        snapshots: [{
          cliType: 'codex',
          fetchedAt: new Date(2026, 7, 23, 20, 55).toISOString(),
          cliVersion: '0.149.0',
          probeFailedAt: failedAt,
          plan: 'Pro',
          windows: [
            { label: '5-hour', usedPct: 38, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '23:10' },
            { label: 'Weekly', usedPct: 72, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:12 on 1 Sep' },
          ],
          source: '/status',
          rawSample: null,
          error: "codex quota probe exceeded its bounded timeout during PTY step 'await-status'.",
        }],
      },
    }));

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1_500);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);

    const card = page.getByTestId('hquota-card-codex');
    await expect(card).toBeVisible();
    await card.click();

    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible();
    await expect(modal.getByText('38% used')).toBeVisible();
    await expect(modal.getByText('72% used')).toBeVisible();
    const stale = modal.getByTestId('cli-usage-probe-stale');
    await expect(stale).toContainText('probe failed 21:07, codex 0.149.0');
    await expect(stale).not.toContainText('bounded timeout');
    await stale.hover();
    await expect(page.getByText(/bounded timeout during PTY step/)).toBeVisible();
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await modal.screenshot({
        path: `${SHOT_DIR}/quota-probe-failure-after--${theme}--mocked.png`,
      });
    }
  });
});
