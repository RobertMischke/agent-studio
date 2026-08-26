import { expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from '../fixtures/dev-backend';

const evidenceDir = process.env.JOB_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'quota-probe-stale');

test.beforeAll(() => fs.mkdirSync(evidenceDir, { recursive: true }));

test('keeps the last-good Codex quota visible when its probe fails', async ({ page, devBackend }) => {
  const endpointStartedAt = performance.now();
  const endpointResponse = await fetch(`${devBackend.baseUrl}/api/cli/quota`);
  const endpointElapsedMs = performance.now() - endpointStartedAt;
  expect(endpointResponse.ok).toBe(true);
  expect(endpointElapsedMs).toBeLessThan(2_000);
  fs.writeFileSync(
    path.join(evidenceDir, 'quota-endpoint-latency.json'),
    `${JSON.stringify({
      measuredAt: new Date().toISOString(),
      endpoint: '/api/cli/quota',
      elapsedMs: Number(endpointElapsedMs.toFixed(2)),
      status: endpointResponse.status,
      upperBoundMs: 2_000,
      cacheState: 'cold process cache; probes started in background',
    }, null, 2)}\n`,
  );

  await page.route('**/api/cli/quota', async route => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        at: '2026-08-23T19:07:30Z',
        ttlSeconds: 600,
        snapshots: [
          {
            cliType: 'claude',
            fetchedAt: '2026-08-23T19:06:00Z',
            plan: 'Max',
            windows: [],
            source: '/usage',
            rawSample: null,
            error: null,
          },
          {
            cliType: 'codex',
            fetchedAt: '2026-08-23T18:55:00Z',
            cliVersion: 'codex-cli 0.149.0',
            probeFailedAt: '2026-08-23T19:07:00Z',
            plan: 'Pro',
            windows: [
              { label: 'Weekly', usedPct: 13, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:12 on 1 Sep' },
              { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '11:26' },
              { label: 'Spark Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '06:26 on 2 Sep' },
            ],
            source: '/status',
            rawSample: null,
            error: 'Codex /status probe timed out before the quota panel was ready.',
          },
        ],
      }),
    });
  });

  await page.goto('/');
  const recoveryOverlay = page.getByTestId('crash-recovery-prompt-overlay');
  await recoveryOverlay.waitFor({ state: 'attached', timeout: 2_000 }).catch(() => undefined);
  if (await recoveryOverlay.count()) {
    await recoveryOverlay.evaluate(element => {
      (element as HTMLElement).style.display = 'none';
      (element as HTMLElement).style.pointerEvents = 'none';
    });
  }

  const codexCard = page.getByTestId('hquota-card-codex');
  await expect(codexCard).toHaveAttribute('data-state', 'error');
  await codexCard.click();

  const modal = page.getByTestId('cli-usage-modal-codex');
  await expect(modal).toBeVisible();
  const stale = modal.getByTestId('cli-usage-stale-marker');
  await expect(stale).toContainText('Stale snapshot');
  await expect(stale).toContainText(/probe failed \d{2}:07, codex 0\.149\.0/);
  await expect(stale).toHaveAttribute(
    'aria-label',
    'Codex /status probe timed out before the quota panel was ready.',
  );
  await expect(modal.getByTestId('cli-usage-window')).toHaveCount(3);
  await expect(modal).not.toContainText('A task was canceled.');
  await modal.screenshot({ path: path.join(evidenceDir, 'quota-after--mocked.png') });
});
