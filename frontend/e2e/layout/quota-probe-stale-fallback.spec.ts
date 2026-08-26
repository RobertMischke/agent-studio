import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync, writeFileSync } from 'node:fs';
import { setTheme } from '../helpers/theme';

const SHOT_DIR = process.env.CODEX_SHOT_DIR?.trim() || 'test-results';
const PROBE_ERROR = 'Quota probe timed out before the CLI panel rendered.';

function legacyFailureReport() {
  return {
    at: '2026-08-23T19:07:00Z',
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'codex',
        fetchedAt: '2026-08-23T19:07:00Z',
        plan: null,
        windows: [],
        source: '/status',
        rawSample: null,
        error: 'A task was canceled.',
      },
    ],
  };
}

function staleLastGoodReport() {
  return {
    at: '2026-08-23T19:07:00Z',
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'codex',
        fetchedAt: '2026-08-23T18:42:00Z',
        cliVersion: 'codex-cli 0.149.0',
        probeFailedAt: '2026-08-23T19:07:00Z',
        plan: 'Pro',
        windows: [
          { label: 'Weekly', usedPct: 51, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:12 on 1 Sep' },
          { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '05:24' },
          { label: 'Spark Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '00:24 on 3 Sep' },
        ],
        source: '/status',
        rawSample: null,
        error: PROBE_ERROR,
      },
    ],
  };
}

async function openCodexModal(page: import('@playwright/test').Page): Promise<void> {
  await page.waitForTimeout(1200);
  await page.keyboard.press('Escape');
  const card = page.getByTestId('hquota-card-codex');
  await expect(card).toBeVisible();
  await card.click();
  await expect(page.getByTestId('cli-usage-modal-codex')).toBeVisible();
}

async function measureQuotaEndpoint(baseUrl: string): Promise<number[]> {
  const durationsMs: number[] = [];
  for (let attempt = 0; attempt < 5; attempt++) {
    const started = performance.now();
    const response = await fetch(`${baseUrl}/api/cli/quota`);
    durationsMs.push(Number((performance.now() - started).toFixed(2)));
    expect(response.ok).toBe(true);
    await response.body?.cancel();
  }
  writeFileSync(
    `${SHOT_DIR}/quota-endpoint-latency.json`,
    JSON.stringify({
      measuredAt: new Date().toISOString(),
      requestCount: durationsMs.length,
      durationsMs,
      maxMs: Math.max(...durationsMs),
      contract: 'GET /api/cli/quota serves cached data while live probes run in the background',
    }, null, 2),
  );
  return durationsMs;
}

test.describe('Quota probe graceful degradation evidence', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.route('**/api/crash-recovery/pending', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ pending: [] }),
    }));
  });

  test('before: a legacy failed probe replaces quota values with a cancellation error', async ({ page }) => {
    await page.route('**/api/cli/quota', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(legacyFailureReport()),
    }));
    await page.goto('/');
    await openCodexModal(page);

    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toContainText('A task was canceled.');
    await expect(modal.getByTestId('cli-usage-modal-windows')).toHaveCount(0);
    await setTheme(page, 'dark');
    await modal.screenshot({ path: `${SHOT_DIR}/quota-probe-before--dark--mocked.png` });
  });

  test('after: last-good values remain with a versioned stale marker and error tooltip', async ({ page, devBackend: _devBackend }) => {
    const endpointDurations = await measureQuotaEndpoint(_devBackend.baseUrl);
    expect(Math.max(...endpointDurations)).toBeLessThan(1_000);

    await page.route('**/api/cli/quota', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(staleLastGoodReport()),
    }));
    await page.goto('/');

    const card = page.getByTestId('hquota-card-codex');
    await expect(card).toHaveAttribute('data-state', 'stale');
    await expect(card).toContainText('51%');
    await expect(card.getByTestId('hquota-stale-marker')).toHaveText('stale');
    await openCodexModal(page);

    const modal = page.getByTestId('cli-usage-modal-codex');
    const marker = modal.getByTestId('cli-usage-stale-marker');
    await expect(marker).toContainText('probe failed 21:07, codex 0.149.0');
    await expect(page.getByTestId('cli-usage-modal-windows')).toContainText('51% used');
    await expect(modal).not.toContainText('A task was canceled.');

    await marker.hover();
    await expect(page.getByText(PROBE_ERROR, { exact: true })).toBeVisible();
    await page.mouse.move(800, 700);
    await expect(page.getByText(PROBE_ERROR, { exact: true })).toBeHidden();

    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await modal.screenshot({ path: `${SHOT_DIR}/quota-probe-after--${theme}--mocked.png` });
    }
  });
});
