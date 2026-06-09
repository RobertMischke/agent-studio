import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';

function codexSparkQuotaReport() {
  return {
    at: new Date().toISOString(),
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'codex',
        fetchedAt: new Date().toISOString(),
        plan: 'Plus',
        windows: [
          { label: '5-hour', usedPct: 3, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '20:09' },
          { label: 'Weekly', usedPct: 14, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '23:43 on 11 Jun' },
          { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:25' },
          { label: 'Spark Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '16:25 on 14 Jun' },
        ],
        source: '/status',
        rawSample: null,
        error: null,
      },
    ],
  };
}

test.describe('Status bar quota: Codex Spark windows', () => {
  test('Codex card renders standard and Spark quota windows', async ({ page }) => {
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(codexSparkQuotaReport()),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const card = page.getByTestId('hquota-card-codex');
    await expect(card).toBeVisible();
    await expect(card).toHaveAttribute('aria-label', /Spark 5-hour 0%/);
    await expect(card).toHaveAttribute('aria-label', /Spark Weekly 0%/);

    await expect(page.getByTestId('hquota-codex-5h')).toBeVisible();
    await expect(page.getByTestId('hquota-codex-wk')).toBeVisible();
    await expect(page.getByTestId('hquota-codex-spark-5h')).toBeVisible();
    await expect(page.getByTestId('hquota-codex-spark-wk')).toBeVisible();

    await expect(page.getByTestId('hquota-codex-spark-5h').locator('.hquota__tag')).toContainText('S5H');
    await expect(page.getByTestId('hquota-codex-spark-wk').locator('.hquota__tag')).toContainText('SWK');

    const outDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
    mkdirSync(outDir, { recursive: true });
    await card.screenshot({ path: `${outDir}/codex-spark-quota-card.png` });
  });
});
