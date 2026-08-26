import { writeFileSync } from 'node:fs';
import { expect, test } from '../fixtures/dev-backend';

test('cached quota GET does not wait for a live PTY probe', async ({ devBackend }) => {
  const startedAt = performance.now();
  const response = await fetch(`${devBackend.baseUrl}/api/cli/quota`);
  const elapsedMs = performance.now() - startedAt;

  expect(response.status).toBe(200);
  expect(elapsedMs).toBeLessThan(1_000);

  const resultDir = process.env.JOB_RESULTS_DIR?.trim();
  if (resultDir) {
    writeFileSync(
      `${resultDir}/quota-endpoint-latency.json`,
      JSON.stringify({
        endpoint: '/api/cli/quota',
        status: response.status,
        elapsedMs: Number(elapsedMs.toFixed(2)),
        measuredAt: new Date().toISOString(),
        boundMs: 1_000,
      }, null, 2),
    );
  }
});
