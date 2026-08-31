import { describe, expect, it } from 'vitest';
import type { QuotaSnapshot } from './models/quota.model';
import { quotaProbeFailureLabel, quotaSafeProbeError, quotaSnapshotIsStale } from './quota-freshness.util';

const snapshot: QuotaSnapshot = {
  cliType: 'codex',
  capturedAt: '2026-08-27T18:00:00Z',
  fetchedAt: '2026-08-27T18:00:00Z',
  ageSeconds: 4020,
  stale: true,
  probeFailed: true,
  cliVersion: 'codex-cli 0.149.0',
  probeFailedAt: '2026-08-27T19:07:00Z',
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
    expect(label).toContain('stale since');
    expect(label).toContain('probe failed');
    expect(label).toContain('codex 0.149.0');
  });

  it('trusts the backend staleness verdict when it is present', () => {
    expect(quotaSnapshotIsStale({ ...snapshot, stale: false }, 1, Date.parse('2026-08-28T19:07:01Z'))).toBe(false);
  });

  it('never exposes cancellation plumbing as operator copy', () => {
    expect(quotaSafeProbeError('A task was canceled.')).toBe('Quota probe timed out before the CLI panel rendered.');
  });
});
