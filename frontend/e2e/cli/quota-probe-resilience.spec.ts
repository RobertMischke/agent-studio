import { mkdirSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { expect, test } from '../fixtures/dev-backend';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const resultsDir = process.env.JOB_RESULTS_DIR?.trim() || 'test-results';

test.describe('CLI quota probe resilience', () => {
  test('cached GET returns without waiting for a live probe', async ({ devBackend }, testInfo) => {
    const started = performance.now();
    const response = await fetch(`${devBackend.baseUrl}/api/cli/quota`, {
      signal: AbortSignal.timeout(5_000),
    });
    const elapsedMs = performance.now() - started;

    expect(response.ok).toBe(true);
    expect(elapsedMs).toBeLessThan(2_000);
    const report = await response.json() as { snapshots?: unknown[] };
    expect(Array.isArray(report.snapshots)).toBe(true);

    const evidence = JSON.stringify({
      endpoint: '/api/cli/quota',
      elapsedMs: Math.round(elapsedMs * 100) / 100,
      status: response.status,
      measuredAt: new Date().toISOString(),
    }, null, 2);
    await testInfo.attach('quota-endpoint-latency.json', {
      body: Buffer.from(evidence),
      contentType: 'application/json',
    });
    mkdirSync(resultsDir, { recursive: true });
    writeFileSync(path.join(resultsDir, 'quota-endpoint-latency.json'), evidence);
  });

  test('live Codex probe records the expected CLI version when requested', async ({ devBackend }, testInfo) => {
    test.setTimeout(90_000);
    const expected = process.env.EXPECTED_CODEX_CLI_VERSION?.trim();
    test.skip(!expected, 'Set EXPECTED_CODEX_CLI_VERSION for the machine-bound live PTY probe.');

    const registration = await fetch(`${devBackend.baseUrl}/api/clients/register`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ displayName: `quota-probe-evidence-${Date.now().toString(36)}` }),
    });
    const registrationText = await registration.text();
    expect(registration.ok, registrationText).toBe(true);
    const client = JSON.parse(registrationText) as { id: string };

    const response = await fetch(`${devBackend.baseUrl}/api/cli/quota/refresh/codex`, {
      method: 'POST',
      headers: { 'X-Client-Id': client.id },
      signal: AbortSignal.timeout(70_000),
    });
    const responseText = await response.text();
    expect(response.ok, `HTTP ${response.status}: ${responseText}`).toBe(true);
    const snapshot = JSON.parse(responseText) as {
      cliVersion?: string;
      error?: string | null;
      rawSample?: string | null;
      windows?: Array<{ label: string; usedPct: number | null }>;
    };

    expect(snapshot.cliVersion).toContain(expected!);
    expect(snapshot.error).toBeNull();
    expect(snapshot.windows?.some(window => window.label === 'Weekly')).toBe(true);

    const sanitizedSnapshot = {
      ...snapshot,
      rawSample: snapshot.rawSample
        ?.replace(/[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}/g, '<redacted-email>')
        .replace(/[0-9a-f]{8}-[0-9a-f-]{27,}/gi, '<redacted-session>'),
    };
    const evidence = JSON.stringify(sanitizedSnapshot, null, 2);
    await testInfo.attach('codex-live-probe.json', {
      body: Buffer.from(evidence),
      contentType: 'application/json',
    });
    mkdirSync(resultsDir, { recursive: true });
    writeFileSync(path.join(resultsDir, 'codex-live-probe.json'), evidence);
    writeFileSync(path.join(resultsDir, 'codex-live-probe-http.json'), JSON.stringify({
      status: response.status,
      cliVersion: snapshot.cliVersion,
      error: snapshot.error,
      windowLabels: snapshot.windows?.map(window => window.label),
    }, null, 2));
  });

  test('shows last-good values with an attributable stale marker and error tooltip', async ({ page, devBackend: _ }, testInfo) => {
    const report = {
      at: new Date().toISOString(),
      ttlSeconds: 600,
      snapshots: [{
        cliType: 'codex',
        cliVersion: 'codex-cli 0.149.0',
        fetchedAt: '2026-08-23T20:55:00Z',
        lastProbeAt: '2026-08-23T19:07:00Z',
        probeFailedAt: '2026-08-23T19:07:00Z',
        plan: 'Pro',
        source: '/status',
        rawSample: null,
        error: 'Timed out waiting for the Codex /status panel.',
        windows: [
          { label: '5-hour', usedPct: 41, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '22:30' },
          { label: 'Weekly', usedPct: 56, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:12 on 1 Sep' },
        ],
      }],
    };

    await page.route('**/api/cli/quota**', async route => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill({ json: report });
    });
    await page.route('**/api/auth/status', route => route.fulfill({
      json: {
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      },
    }));
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');

    const codexCard = page.getByTestId('hquota-card-codex');
    await expect(codexCard).toBeVisible();
    await expect(page.getByTestId('hquota-codex-5h')).toContainText('41%');
    await expect(page.getByTestId('hquota-codex-wk')).toContainText('56%');
    const headerFailure = page.getByTestId('hquota-probe-failure');
    await expect(headerFailure).toContainText(/probe failed .*codex 0\.149\.0/);

    await dismissDevErrorDialog(page);
    await codexCard.click();
    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible();
    const staleMarker = page.getByTestId('cli-usage-probe-failure');
    await expect(staleMarker).toContainText(/probe failed .*codex 0\.149\.0/);
    await expect(modal.getByText('41% used')).toBeVisible();
    await expect(modal.getByText('56% used')).toBeVisible();
    await expect(modal.getByText('A task was canceled.')).toHaveCount(0);

    await staleMarker.hover();
    await expect(page.locator('.app-tooltip-overlay')).toContainText('Timed out waiting for the Codex /status panel.');

    // Faithful mocked reproduction of the operator's old generic-error sighting.
    await setTheme(page, 'dark');
    const correctedFailureText = await staleMarker.textContent();
    await staleMarker.evaluate(element => { element.textContent = 'A task was canceled.'; });
    const before = await modal.screenshot();
    await testInfo.attach('quota-probe-before--mocked.png', { body: before, contentType: 'image/png' });
    mkdirSync(resultsDir, { recursive: true });
    writeFileSync(path.join(resultsDir, 'quota-probe-before--mocked.png'), before);

    await staleMarker.evaluate((element, text) => { element.textContent = text; }, correctedFailureText);
    await page.mouse.move(0, 0);
    const after = await modal.screenshot();
    await testInfo.attach('quota-probe-after--dark--mocked.png', { body: after, contentType: 'image/png' });
    writeFileSync(path.join(resultsDir, 'quota-probe-after--dark--mocked.png'), after);

    await setTheme(page, 'light');
    const afterLight = await modal.screenshot();
    await testInfo.attach('quota-probe-after--light--mocked.png', { body: afterLight, contentType: 'image/png' });
    writeFileSync(path.join(resultsDir, 'quota-probe-after--light--mocked.png'), afterLight);
  });
});
