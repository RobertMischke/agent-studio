import { mkdirSync, writeFileSync } from 'node:fs';
import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';

type Stage = 'before' | 'after';

function quotaReport(stage: Stage) {
  const common = {
    cliType: 'codex',
    fetchedAt: '2026-08-23T18:42:00Z',
    plan: 'Pro',
    source: '/status',
    rawSample: null,
  };
  const codex = stage === 'before'
    ? { ...common, windows: [], error: 'A task was canceled.' }
    : {
        ...common,
        cliVersion: 'codex-cli 0.149.0',
        probeFailedAt: '2026-08-23T19:07:00Z',
        error: 'Codex quota probe timed out before /status finished rendering.',
        windows: [
          { label: 'Weekly', usedPct: 29, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:12 on 1 Sep' },
          { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '22:07' },
          { label: 'Spark Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:07 on 2 Sep' },
        ],
      };
  return { at: new Date().toISOString(), ttlSeconds: 600, snapshots: [codex] };
}

async function setTheme(page: Page, theme: 'light' | 'dark') {
  await page.evaluate((value) => { document.documentElement.dataset['studioTheme'] = value; }, theme);
  await page.waitForTimeout(100);
}

test('retains quota values with a versioned stale marker and error tooltip', async ({ page, request, devBackend }) => {
  let stage: Stage = 'before';
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.route('**/api/cli/quota', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(quotaReport(stage)),
  }));
  await page.setViewportSize({ width: 1600, height: 900 });
  const shotDir = process.env['JOB_RESULTS_DIR'] ?? 'test-results';
  mkdirSync(shotDir, { recursive: true });

  const latencyMs: number[] = [];
  for (let i = 0; i < 10; i++) {
    const started = performance.now();
    const response = await request.get(`${devBackend.baseUrl}/api/cli/quota`);
    latencyMs.push(Number((performance.now() - started).toFixed(2)));
    expect(response.ok()).toBe(true);
  }
  const sorted = [...latencyMs].sort((a, b) => a - b);
  const latencyEvidence = {
    endpoint: `${devBackend.baseUrl}/api/cli/quota`,
    samples: latencyMs,
    medianMs: sorted[Math.floor(sorted.length / 2)],
    maxMs: sorted.at(-1),
  };
  writeFileSync(
    `${shotDir}/quota-endpoint-latency.json`,
    JSON.stringify(latencyEvidence, null, 2) + '\n',
    'utf8',
  );
  expect(latencyEvidence.maxMs).toBeLessThan(1_000);

  await page.goto('/');
  await expect(page.getByTestId('hquota-card-codex')).toBeVisible();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('status-bar').screenshot({
      path: `${shotDir}/quota-probe-failure-${theme}--before--mocked.png`,
    });
  }

  stage = 'after';
  await page.reload();
  const marker = page.getByTestId('hquota-probe-failed');
  await expect(page.getByTestId('hquota-codex-wk').locator('.hquota__value')).toHaveText('29%');
  await expect(marker).toContainText(/probe failed \d{2}:\d{2}, codex 0\.149\.0/);
  await marker.hover();
  await expect(page.getByTestId('hquota-probe-error-tooltip')).toContainText(
    'Codex quota probe timed out before /status finished rendering.',
  );
  await expect(page.getByTestId('hquota-probe-error-tooltip')).not.toContainText('A task was canceled');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('status-bar').screenshot({
      path: `${shotDir}/quota-probe-failure-${theme}--after--mocked.png`,
    });
  }
});
