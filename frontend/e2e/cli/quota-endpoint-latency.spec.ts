import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

test('GET /api/cli/quota serves cache without awaiting live PTY probes', async ({ devBackend }) => {
  const samplesMs: number[] = [];
  for (let index = 0; index < 5; index++) {
    const started = performance.now();
    const response = await fetch(`${devBackend.baseUrl}/api/cli/quota`);
    samplesMs.push(performance.now() - started);
    expect(response.status).toBe(200);
    const report = await response.json() as { snapshots?: unknown[] };
    expect(Array.isArray(report.snapshots)).toBe(true);
  }

  const maxMs = Math.max(...samplesMs);
  expect(maxMs, `cached quota GET max latency was ${maxMs.toFixed(1)} ms`).toBeLessThan(1000);

  const resultsDir = process.env.JOB_RESULTS_DIR?.trim();
  if (resultsDir) {
    mkdirSync(resultsDir, { recursive: true });
    writeFileSync(join(resultsDir, 'quota-endpoint-latency.json'), JSON.stringify({
      measuredAt: new Date().toISOString(),
      endpoint: '/api/cli/quota',
      samplesMs: samplesMs.map(value => Number(value.toFixed(2))),
      maxMs: Number(maxMs.toFixed(2)),
      thresholdMs: 1000,
    }, null, 2));
  }
});
