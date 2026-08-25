import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const SHOT_DIR = process.env.JOB_RESULTS_DIR?.trim() || 'test-results';

function quotaReport(attributable: boolean) {
  return {
    at: '2026-08-25T19:07:01Z',
    ttlSeconds: 600,
    snapshots: [
      attributable
        ? {
            cliType: 'codex',
            fetchedAt: '2026-08-25T18:55:00Z',
            probeFailedAt: '2026-08-25T19:07:00Z',
            cliVersion: 'codex-cli 0.149.0',
            plan: 'Pro',
            windows: [
              { label: 'Weekly', usedPct: 6, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '08:08 on 31 Aug' },
              { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '16:58' },
            ],
            source: '/status',
            rawSample: null,
            error: 'Codex /status probe timed out before the quota panel was captured.',
          }
        : {
            cliType: 'codex',
            fetchedAt: '2026-08-25T19:07:00Z',
            plan: null,
            windows: [],
            source: '/status',
            rawSample: null,
            error: 'A task was canceled.',
          },
    ],
  };
}

test('failed Codex probe retains values, shows versioned stale marker, and exposes the error tooltip', async ({ page }) => {
  mkdirSync(SHOT_DIR, { recursive: true });
  let attributable = false;
  await page.route('**/api/auth/status', async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'networked', bootstrapRequired: false, authenticated: true,
        user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
      }),
    });
  });
  await page.route('**/api/cli/quota', async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(quotaReport(attributable)) });
  });
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  const card = page.getByTestId('hquota-card-codex');
  await expect(card).toBeVisible();
  await setTheme(page, 'dark');
  await card.screenshot({ path: join(SHOT_DIR, 'quota-probe-failure-before--dark--mocked.png') });

  attributable = true;
  await page.reload();
  // A frontend-only dev server can surface an unrelated startup request
  // failure. Dismiss that global dialog so it cannot intercept the marker's
  // hover path; the quota endpoint itself remains the controlled subject.
  await page.waitForTimeout(1200);
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('hquota-codex-wk')).toContainText('6%');
  await expect(page.getByTestId('hquota-codex-spark-5h')).toContainText('0%');
  const marker = page.getByTestId('hquota-probe-failed-codex');
  await expect(marker).toContainText(/probe failed .*codex 0\.149\.0/);
  await expect(card).toHaveAttribute('data-state', 'error');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await card.screenshot({ path: join(SHOT_DIR, `quota-probe-failure-after--${theme}--mocked.png`) });
  }

  await setTheme(page, 'dark');
  await marker.hover();
  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toContainText('Codex /status probe timed out before the quota panel was captured.');
  await expect(tooltip).not.toContainText('A task was canceled.');
  await page.screenshot({ path: join(SHOT_DIR, 'quota-probe-failure-after-tooltip--dark--mocked.png') });
});
