import { test, expect } from '../fixtures/dev-backend';

test.describe('Quota cache endpoint latency', () => {
  test('GET /api/cli/quota serves cached data without waiting for a live probe', async ({ devBackend }, testInfo) => {
    const samples: number[] = [];
    let lastReport: {
      snapshots?: { capturedAt?: string; isStale?: boolean; ageSeconds?: number }[];
    } = {};
    for (let attempt = 0; attempt < 5; attempt++) {
      const started = performance.now();
      const response = await fetch(`${devBackend.baseUrl}/api/cli/quota`, {
        signal: AbortSignal.timeout(5_000),
      });
      samples.push(performance.now() - started);
      expect(response.status).toBe(200);
      lastReport = await response.json();
    }

    const evidence = {
      samplesMs: samples.map(value => Number(value.toFixed(1))),
      maxMs: Number(Math.max(...samples).toFixed(1)),
      medianMs: Number([...samples].sort((a, b) => a - b)[Math.floor(samples.length / 2)].toFixed(1)),
    };
    await testInfo.attach('quota-endpoint-latency.json', {
      body: Buffer.from(JSON.stringify(evidence, null, 2)),
      contentType: 'application/json',
    });
    console.log(`quota endpoint latency: ${JSON.stringify(evidence)}`);
    expect(evidence.maxMs, 'cached quota GET must not wait for a PTY probe').toBeLessThan(1_500);
    expect(lastReport.snapshots?.length).toBeGreaterThan(0);
    for (const snapshot of lastReport.snapshots ?? []) {
      expect(snapshot.capturedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
      expect(typeof snapshot.isStale).toBe('boolean');
      expect(typeof snapshot.ageSeconds).toBe('number');
    }
  });
});
