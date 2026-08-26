import { describe, expect, it } from 'vitest';
import type { QuotaSnapshot } from './quota.model';
import { quotaFreshness } from './quota-freshness';

describe('quotaFreshness', () => {
  const snapshot: QuotaSnapshot = {
    cliType: 'codex',
    fetchedAt: new Date(2026, 7, 23, 20, 0).toISOString(),
    cliVersion: 'codex-cli 0.149.0',
    probeFailedAt: new Date(2026, 7, 23, 21, 7).toISOString(),
    plan: 'Pro',
    windows: [],
    source: '/status',
    rawSample: null,
    error: 'Quota probe timed out before the CLI panel rendered.',
  };

  it('marks retained values stale and attributes the failed CLI version', () => {
    const result = quotaFreshness(snapshot, 600_000, Date.now());

    expect(result.stale).toBe(true);
    expect(result.label).toBe('probe failed 21:07, codex 0.149.0');
  });

  it('uses the normal TTL when no probe failed', () => {
    const fetchedAt = new Date(2026, 7, 23, 21, 6).toISOString();
    const result = quotaFreshness(
      { ...snapshot, fetchedAt, probeFailedAt: null, error: null },
      600_000,
      new Date(2026, 7, 23, 21, 7).getTime(),
    );

    expect(result).toEqual({ stale: false, label: 'updated 1 min ago' });
  });
});
