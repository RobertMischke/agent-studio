import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { CliUsageStore } from './cli-usage.store';
import type { QuotaReport, QuotaSnapshot } from '../../quota';

/**
 * AGT-2679. The operator's quota display used to replace its numbers with the
 * raw text "A task was canceled." whenever a probe failed. The backend now keeps
 * the last-good windows and flags the snapshot stale; these tests pin that the
 * store turns that into a readable row instead of an empty one.
 */
describe('CliUsageStore quota row degradation', () => {
  let store: CliUsageStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    store = TestBed.inject(CliUsageStore);
  });

  function publish(snapshot: QuotaSnapshot): void {
    const report: QuotaReport = { at: new Date().toISOString(), ttlSeconds: 600, snapshots: [snapshot] };
    store.report.set(report);
  }

  const degraded: QuotaSnapshot = {
    cliType: 'codex',
    // The failed probe's own timestamp - what "probe failed HH:MM" reports.
    fetchedAt: new Date('2026-08-23T21:07:00Z').toISOString(),
    plan: 'Pro',
    source: '/status',
    rawSample: null,
    error: 'Quota probe timed out driving the codex TUI (codex-cli 0.149.0).',
    stale: true,
    lastGoodAt: new Date('2026-08-23T20:57:00Z').toISOString(),
    cliVersion: 'codex-cli 0.149.0',
    windows: [
      { label: '5-hour', usedPct: 42, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '02:33' },
    ],
  };

  it('keeps the last-good windows visible when the probe failed', () => {
    publish(degraded);

    const row = store.quotaRows()[0];
    expect(row.windows).toHaveLength(1);
    expect(row.windows[0].usedPct).toBe(42);
    expect(row.plan).toBe('Pro');
    expect(row.probeFailed).toBe(true);
  });

  it('marks the failure with its time and the CLI version, not the raw exception', () => {
    publish(degraded);

    const marker = store.quotaRows()[0].staleMarker ?? '';
    expect(marker).toContain('probe failed');
    expect(marker).toContain('codex-cli 0.149.0');
    // The stock .NET message must never reach the surface; the detail belongs
    // in the tooltip, which reads `error`.
    expect(marker).not.toContain('A task was canceled');
  });

  it('dates the freshness from the measurement, not from the failed probe', () => {
    publish(degraded);

    // The numbers were measured at lastGoodAt (20:57), ten minutes before the
    // probe that failed at 21:07. Reporting "updated just now" would be a lie.
    expect(store.quotaRows()[0].freshness).toContain('measured');
  });

  it('leaves a healthy snapshot unmarked', () => {
    publish({ ...degraded, stale: false, error: null, lastGoodAt: null, fetchedAt: new Date().toISOString() });

    const row = store.quotaRows()[0];
    expect(row.probeFailed).toBe(false);
    expect(row.staleMarker).toBeNull();
    expect(row.freshness).toContain('updated');
  });

  it('does not claim stale data when a probe failed with nothing cached', () => {
    publish({ ...degraded, stale: false, windows: [], plan: null, lastGoodAt: null });

    const row = store.quotaRows()[0];
    expect(row.probeFailed).toBe(false);
    expect(row.staleMarker).toBeNull();
    expect(row.windows).toHaveLength(0);
    // The error is still available for the error branch of the template.
    expect(row.error).toContain('timed out');
  });
});
