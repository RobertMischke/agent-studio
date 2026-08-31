import { describe, expect, it } from 'vitest';
import type { QuotaSnapshot } from './models/quota.model';
import { quotaProbeFailureLabel, quotaSnapshotIsStale, quotaVisibleError } from './quota-freshness.util';

const snapshot: QuotaSnapshot = {
  cliType: 'codex',
  fetchedAt: '2026-08-27T18:00:00Z',
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
    expect(label).toContain('Stale since');
    expect(label).toContain('probe failed');
    expect(label).toContain('Codex 0.149.0');
  });

  it('never exposes cancellation implementation wording from an older backend', () => {
    expect(quotaVisibleError('A task was canceled.'))
      .toBe('Quota probe timed out before the CLI panel rendered.');
  });
});
