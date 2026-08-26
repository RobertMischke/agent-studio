import { describe, expect, it } from 'vitest';
import type { QuotaSnapshot } from './quota.model';
import { buildQuotaFreshness } from './quota-freshness';

describe('buildQuotaFreshness', () => {
  it('attributes a failed Codex probe and marks retained values stale', () => {
    const snapshot: QuotaSnapshot = {
      cliType: 'codex',
      fetchedAt: '2026-08-23T18:55:00Z',
      cliVersion: 'codex-cli 0.149.0',
      probeFailedAt: '2026-08-23T19:07:00Z',
      plan: 'Pro',
      windows: [],
      source: '/status',
      rawSample: null,
      error: 'Codex /status probe timed out before the quota panel was ready.',
    };

    const result = buildQuotaFreshness(snapshot, 600_000, Date.parse('2026-08-23T19:08:00Z'), 'en-GB', 'UTC');

    expect(result.stale).toBe(true);
    expect(result.label).toBe('probe failed 19:07, codex 0.149.0');
    expect(result.tooltip).toContain(snapshot.error);
  });

  it('uses normal TTL freshness when the last probe succeeded', () => {
    const snapshot: QuotaSnapshot = {
      cliType: 'claude',
      fetchedAt: '2026-08-23T19:00:00Z',
      plan: 'Max',
      windows: [],
      source: '/usage',
      rawSample: null,
      error: null,
    };

    const result = buildQuotaFreshness(snapshot, 600_000, Date.parse('2026-08-23T19:02:00Z'));

    expect(result.stale).toBe(false);
    expect(result.label).toBe('updated 2 min ago');
  });
});
