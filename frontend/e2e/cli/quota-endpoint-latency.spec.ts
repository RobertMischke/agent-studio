import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

test.describe('CLI quota cached endpoint', () => {
  test('returns cached quota without waiting for a live PTY probe', async ({ devBackend }) => {
    const started = performance.now();
    const response = await fetch(`${devBackend.baseUrl}/api/cli/quota`, {
      signal: AbortSignal.timeout(5_000),
    });
    const elapsedMs = performance.now() - started;

    expect(response.ok).toBe(true);
    const report = await response.json() as { snapshots?: unknown[] };
    expect(Array.isArray(report.snapshots)).toBe(true);
    expect(elapsedMs).toBeLessThan(1_000);

    const resultsDir = process.env.JOB_RESULTS_DIR?.trim();
    if (resultsDir) {
      mkdirSync(resultsDir, { recursive: true });
      writeFileSync(join(resultsDir, 'quota-endpoint-latency.json'), JSON.stringify({
        measuredAt: new Date().toISOString(),
        endpoint: '/api/cli/quota',
        elapsedMs: Math.round(elapsedMs * 10) / 10,
        budgetMs: 1_000,
        status: response.status,
      }, null, 2));
    }
  });
});
