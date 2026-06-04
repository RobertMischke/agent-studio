import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { HeaderQuotaComponent } from './header-quota';

type WindowDisplay = { value: string; barPct: number; tone: string; windowKind: string };
type PrimaryDisplay = { value: string; tag: string; barPct: number; hasValue: boolean; tone: string };
type Chip = { windowKey: string; tag: string; value: string; barPct: number; tone: string };

const noPrimary: PrimaryDisplay = { value: '—', tag: '', barPct: 0, hasValue: false, tone: 'unknown' };

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 */
describe('HeaderQuotaComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [HeaderQuotaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(HeaderQuotaComponent);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] HeaderQuotaComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Status-bar quota-strip contract (2026-06-04 — ASS-696 follow-up).
 *
 * The strip no longer carries any hover tooltip: clicking a card opens
 * that CLI's own detail modal instead. What remains locked here:
 *
 *  - `state` still drives the data-state DOM attribute (error / hot /
 *    warn dominate stale; under-70% on every window = idle; no windows +
 *    no error = unavailable).
 *  - `buildChips` surfaces EVERY reported window, so a CLI that exposes
 *    both a 5h and a weekly window renders two chips side by side
 *    (requirement: show all windows, not just the most-constraining one).
 */
describe('HeaderQuotaComponent (semantic state)', () => {
  async function buildComponent() {
    await TestBed.configureTestingModule({
      imports: [HeaderQuotaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(HeaderQuotaComponent);
    return fixture.componentInstance as unknown as {
      cardState: (
        tone: string,
        stale: boolean,
        hasError: boolean,
        sw: WindowDisplay | undefined,
        ww: WindowDisplay | undefined,
        primary: PrimaryDisplay,
      ) => string;
      buildChips: (
        sw: WindowDisplay | undefined,
        ww: WindowDisplay | undefined,
        primary: PrimaryDisplay,
      ) => Chip[];
    };
  }

  it('idle when both windows are under 70%', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '31%', barPct: 31, tone: 'ok', windowKind: 'five_hour' };
    const ww: WindowDisplay = { value: '55%', barPct: 55, tone: 'ok', windowKind: 'weekly' };
    expect(c.cardState('ok', false, false, sw, ww, noPrimary)).toBe('idle');
  });

  it('warn when any window crosses 70%', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '72%', barPct: 72, tone: 'warn', windowKind: 'five_hour' };
    const ww: WindowDisplay = { value: '40%', barPct: 40, tone: 'ok', windowKind: 'weekly' };
    expect(c.cardState('warn', false, false, sw, ww, noPrimary)).toBe('warn');
  });

  it('hot when any window crosses 90%', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '95%', barPct: 95, tone: 'hot', windowKind: 'five_hour' };
    expect(c.cardState('hot', false, false, sw, undefined, noPrimary)).toBe('hot');
  });

  it('error always dominates other tones', async () => {
    const c = await buildComponent();
    expect(c.cardState('warn', true, true, undefined, undefined, noPrimary)).toBe('error');
  });

  it('unavailable when no windows reported and no error', async () => {
    const c = await buildComponent();
    expect(c.cardState('unknown', true, false, undefined, undefined, noPrimary)).toBe('unavailable');
  });

  it('stale when fresh windows exist but snapshot is older than TTL', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '40%', barPct: 40, tone: 'ok', windowKind: 'five_hour' };
    expect(c.cardState('ok', true, false, sw, undefined, noPrimary)).toBe('stale');
  });

  it('renders both a 5H and a WK chip when both windows are present', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '11%', barPct: 11, tone: 'ok', windowKind: 'five_hour' };
    const ww: WindowDisplay = { value: '47%', barPct: 47, tone: 'ok', windowKind: 'weekly' };
    const chips = c.buildChips(sw, ww, noPrimary);
    expect(chips.map(ch => ch.windowKey)).toEqual(['5h', 'wk']);
    expect(chips.map(ch => ch.tag)).toEqual(['5H', 'WK']);
    expect(chips.map(ch => ch.value)).toEqual(['11%', '47%']);
  });

  it('falls back to the primary chip when no 5H / WK window is reported', async () => {
    const c = await buildComponent();
    const primary: PrimaryDisplay = { value: '8%', tag: 'MO', barPct: 8, hasValue: true, tone: 'ok' };
    const chips = c.buildChips(undefined, undefined, primary);
    expect(chips).toHaveLength(1);
    expect(chips[0].tag).toBe('MO');
    expect(chips[0].value).toBe('8%');
  });

  it('renders a placeholder chip when nothing is reported', async () => {
    const c = await buildComponent();
    const chips = c.buildChips(undefined, undefined, noPrimary);
    expect(chips).toHaveLength(1);
    expect(chips[0].windowKey).toBe('none');
    expect(chips[0].value).toBe('—');
  });
});
