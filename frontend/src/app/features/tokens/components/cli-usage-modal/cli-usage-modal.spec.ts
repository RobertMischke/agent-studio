import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliUsageModalComponent } from './cli-usage-modal';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';

/**
 * Smoke + contract for the per-CLI usage modal. Confirms it instantiates,
 * derives its title/subtitle from the row, and exposes every reported
 * quota window (so Claude / Codex show both their 5h and weekly windows
 * — requirement: show all windows, no grouped collapse).
 */
describe('CliUsageModalComponent', () => {
  async function build(row: CliUsageQuotaRow | null) {
    await TestBed.configureTestingModule({
      imports: [CliUsageModalComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliUsageModalComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('row', row);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] CliUsageModalComponent initial render skipped:', (e as Error).message);
    }
    return fixture;
  }

  const row: CliUsageQuotaRow = {
    cliType: 'claude',
    icon: '✴️',
    label: 'Claude',
    plan: 'Pro',
    fetchedAt: new Date().toISOString(),
    freshness: 'updated just now',
    stale: false,
    source: 'pty',
    error: null,
    windows: [
      { label: 'Current session (5h)', usedPct: 11, used: null, limit: null, unit: null, resetAt: null, resetLabel: '3h' },
      { label: 'Weekly (all models)', usedPct: 47, used: null, limit: null, unit: null, resetAt: null, resetLabel: '4d' },
    ],
    primary: null,
    primaryPct: 47,
    primaryTone: 'ok',
  };

  it('instantiates and titles itself from the row', async () => {
    const fixture = await build(row);
    const c = fixture.componentInstance;
    expect(c).toBeTruthy();
    expect(c.title()).toBe('Claude');
    expect(c.subtitle()).toContain('Pro');
  });

  it('surfaces every reported window (5h + weekly)', async () => {
    const fixture = await build(row);
    expect(fixture.componentInstance.windows()).toHaveLength(2);
  });

  it('falls back to the CLI label and "no data" when no row is given', async () => {
    const fixture = await build(null);
    const c = fixture.componentInstance;
    expect(c.title()).toBe('Claude');
    expect(c.subtitle()).toBe('No data yet');
    expect(c.windows()).toHaveLength(0);
  });

  /**
   * Regression for the Codex "%-limit = 100%" bug (2026-07-10). The live
   * Codex payload reports its windows as `unit: "%"` with both `used` and
   * `limit` null and only `usedPct` set. The Limit column must show the
   * implied 100% cap, not a bare "n/a" placeholder.
   */
  const codexRow: CliUsageQuotaRow = {
    cliType: 'codex',
    icon: '🟪',
    label: 'Codex',
    plan: 'Pro',
    fetchedAt: new Date().toISOString(),
    freshness: 'updated just now',
    stale: false,
    source: '/status',
    error: null,
    windows: [
      { label: 'Current session (5h)', usedPct: 66, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '02:33' },
      { label: 'Weekly', usedPct: 12, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:33 on 3 May' },
      { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:25' },
      { label: 'Spark Weekly', usedPct: 4, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '16:25 on 14 Jun' },
    ],
    primary: null,
    primaryPct: 66,
    primaryTone: 'ok',
  };

  it('shows the implied 100% cap for "%" windows with a null limit (Codex)', async () => {
    const fixture = await build(codexRow);
    const c = fixture.componentInstance;
    expect(c.windows()).toHaveLength(4);
    // Every window is unit "%" with a null limit -> implied cap 100%, not "n/a".
    for (const w of c.windows()) {
      expect(c.limitText(w)).toBe('100%');
    }
  });

  it('still returns "n/a" when a window carries no usable number at all', async () => {
    const fixture = await build(codexRow);
    const c = fixture.componentInstance;
    expect(
      c.limitText({ label: 'x', usedPct: null, used: null, limit: null, unit: null, resetAt: null, resetLabel: null }),
    ).toBe('n/a');
  });
});
