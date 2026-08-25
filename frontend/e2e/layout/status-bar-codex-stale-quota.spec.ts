import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync, writeFileSync } from 'node:fs';
import { setTheme, type Theme } from '../helpers/theme';

const SHOT_DIR = process.env.CODEX_SHOT_DIR?.trim() || 'test-results';
const THEMES: readonly Theme[] = ['dark', 'light'];

function staleCodexQuotaReport() {
  return {
    at: '2026-08-23T19:07:01Z',
    ttlSeconds: 600,
    snapshots: [{
      cliType: 'codex',
      cliVersion: 'codex-cli 0.149.0',
      fetchedAt: '2026-08-23T18:55:00Z',
      probeFailedAt: '2026-08-23T19:07:00Z',
      plan: 'Pro',
      windows: [
        { label: 'Weekly', usedPct: 5, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:12 on 1 Sep' },
        { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '04:22 on 26 Aug' },
        { label: 'Spark Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '23:22 on 1 Sep' },
      ],
      source: '/status',
      rawSample: null,
      error: 'codex quota probe timed out while waiting for /status.',
    }],
  };
}

test.describe('Status bar quota: failed Codex probe degrades to last-good data', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.route('**/api/cli/quota', async route => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(staleCodexQuotaReport()),
      });
    });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
  });

  test('retains percentages and exposes the attributed stale marker in both themes', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    const latencyStarted = performance.now();
    const quotaResponse = await fetch(`${devBackend.baseUrl}/api/cli/quota`);
    const latencyMs = performance.now() - latencyStarted;
    expect(quotaResponse.ok).toBe(true);
    expect(latencyMs).toBeLessThan(1_000);
    writeFileSync(
      `${SHOT_DIR}/quota-endpoint-latency.json`,
      JSON.stringify({ endpoint: '/api/cli/quota', latencyMs: Number(latencyMs.toFixed(1)), status: quotaResponse.status }, null, 2),
    );
    const card = page.getByTestId('hquota-card-codex');
    await expect(card).toBeVisible();
    await expect(page.getByTestId('hquota-codex-wk')).toContainText('5%');
    await expect(page.getByTestId('hquota-codex-spark-5h')).toContainText('0%');
    await expect(page.getByTestId('hquota-stale-marker')).toHaveText('stale');

    const recoveryPrompt = page.getByTestId('crash-recovery-prompt-overlay');
    if (await recoveryPrompt.isVisible()) {
      await recoveryPrompt.getByRole('button', { name: 'Leave all uncommitted' }).click();
      await expect(recoveryPrompt).toBeHidden();
    }

    await card.click();
    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible();
    const marker = page.getByTestId('cli-usage-stale-marker');
    await expect(marker).toContainText(/probe failed .+, codex 0\.149\.0/);
    await expect(marker).toContainText('Last-good quota values shown');
    await expect(marker).not.toContainText('A task was canceled');
    await expect(page.getByTestId('cli-usage-modal-windows')).toContainText('5% used');

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await modal.screenshot({
        path: `${SHOT_DIR}/quota-probe-after--${theme}--mocked.png`,
      });
    }
  });
});
