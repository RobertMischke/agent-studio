import { test, expect } from '../fixtures/dev-backend';

test.describe('Quota cache endpoint latency', () => {
  test('GET /api/cli/quota serves cached data without waiting for a live probe', async ({ devBackend }, testInfo) => {
    const samples: number[] = [];
    for (let attempt = 0; attempt < 5; attempt++) {
      const started = performance.now();
      const response = await fetch(`${devBackend.baseUrl}/api/cli/quota`, {
        signal: AbortSignal.timeout(5_000),
      });
      samples.push(performance.now() - started);
      expect(response.status).toBe(200);
      const report = await response.json() as {
        snapshots: Array<{
          capturedAt: string | null;
          ageSeconds: number | null;
          stale: boolean;
          probeFailed: boolean;
          cliVersion: string | null;
        }>;
      };
      expect(report.snapshots.length).toBeGreaterThan(0);
      for (const snapshot of report.snapshots) {
        expect(typeof snapshot.stale).toBe('boolean');
        expect(typeof snapshot.probeFailed).toBe('boolean');
        expect(snapshot.capturedAt === null || typeof snapshot.capturedAt === 'string').toBe(true);
        expect(snapshot.ageSeconds === null || typeof snapshot.ageSeconds === 'number').toBe(true);
        expect(snapshot.cliVersion === null || typeof snapshot.cliVersion === 'string').toBe(true);
      }
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
  });
});
