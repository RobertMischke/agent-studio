import { describe, expect, it } from 'vitest';
import type { QuotaSnapshot } from './models/quota.model';
import { quotaProbeFailureLabel, quotaSnapshotIsStale } from './quota-freshness.util';

const snapshot: QuotaSnapshot = {
  cliType: 'codex',
  fetchedAt: '2026-08-27T18:00:00Z',
  cliVersion: 'codex-cli 0.149.0',
  probeFailedAt: '2026-08-27T19:07:00Z',
  capturedAt: '2026-08-27T18:00:00Z',
  stale: true,
  ageSeconds: 4_020,
  staleSince: '2026-08-27T19:07:00Z',
  plan: 'Pro',
  windows: [],
  source: '/status',
  rawSample: null,
  error: 'Quota probe timed out before the CLI panel rendered.',
};

describe('quota freshness', () => {
  it('marks a last-good snapshot stale immediately after a failed probe', () => {
    expect(quotaSnapshotIsStale(snapshot, 600_000, Date.parse('2026-08-27T19:07:01Z'))).toBe(true);
  });

  it('attributes the failed attempt to the exact CLI version', () => {
    const label = quotaProbeFailureLabel(snapshot);
    expect(label).toContain('stale since 19:07, probe failed');
    expect(label).toContain('codex 0.149.0');
  });

  it('honors the explicit backend stale flag before the local TTL elapses', () => {
    const explicitlyStale = { ...snapshot, probeFailedAt: null, staleSince: null };
    expect(quotaSnapshotIsStale(explicitlyStale, 600_000, Date.parse('2026-08-27T18:00:01Z'))).toBe(true);
  });
});
