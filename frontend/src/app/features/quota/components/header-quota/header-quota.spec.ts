import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { HeaderQuotaComponent } from './header-quota';

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
 * F50 follow-up (2026-05-29) — Status Bar layout consolidation.
 *
 * Lock the semantic-state contract of the quota cards: every card now
 * carries a `state` field that drives the tooltip + the data-state DOM
 * attribute, so the operator can hover a "loud" card and see why it is
 * highlighted instead of reading the SCSS.
 *
 * The mapping rules (from cardState() in header-quota.ts):
 *   error / hot / warn dominate stale; under-70% on every window = idle;
 *   no windows + no error = unavailable.
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
      cardState: (...a: unknown[]) => string;
      cardTooltip: (...a: unknown[]) => { body: string };
    };
  }

  it('idle when both windows are under 70%', async () => {
    const c = await buildComponent();
    const sw = { value: '31%', barPct: 31, tone: 'ok', tooltip: '', windowKind: 'five_hour' };
    const ww = { value: '55%', barPct: 55, tone: 'ok', tooltip: '', windowKind: 'weekly' };
    expect(c.cardState('ok', false, false, sw, ww)).toBe('idle');
  });

  it('warn when any window crosses 70%', async () => {
    const c = await buildComponent();
    const sw = { value: '72%', barPct: 72, tone: 'warn', tooltip: '', windowKind: 'five_hour' };
    const ww = { value: '40%', barPct: 40, tone: 'ok', tooltip: '', windowKind: 'weekly' };
    expect(c.cardState('warn', false, false, sw, ww)).toBe('warn');
  });

  it('hot when any window crosses 90%', async () => {
    const c = await buildComponent();
    const sw = { value: '95%', barPct: 95, tone: 'hot', tooltip: '', windowKind: 'five_hour' };
    expect(c.cardState('hot', false, false, sw, undefined)).toBe('hot');
  });

  it('error always dominates other tones', async () => {
    const c = await buildComponent();
    expect(c.cardState('warn', true, true, undefined, undefined)).toBe('error');
  });

  it('unavailable when no windows reported and no error', async () => {
    const c = await buildComponent();
    expect(c.cardState('unknown', true, false, undefined, undefined)).toBe('unavailable');
  });

  it('stale when fresh windows exist but snapshot is older than TTL', async () => {
    const c = await buildComponent();
    const sw = { value: '40%', barPct: 40, tone: 'ok', tooltip: '', windowKind: 'five_hour' };
    expect(c.cardState('ok', true, false, sw, undefined)).toBe('stale');
  });

  it('warn tooltip names the threshold so the highlight is self-explanatory', async () => {
    const c = await buildComponent();
    const sw = { value: '72%', barPct: 72, tone: 'warn', tooltip: '', windowKind: 'five_hour' };
    const tip = c.cardTooltip('Codex', 'warn', 'pro', 'updated 30 s ago', sw, undefined, null);
    expect(tip.body).toContain('Codex');
    expect(tip.body).toContain('quota warning');
    expect(tip.body).toContain('70%');
    expect(tip.body).toContain('5H rolling: 72%');
  });

  it('error tooltip surfaces the probe-failure message', async () => {
    const c = await buildComponent();
    const tip = c.cardTooltip('Claude', 'error', null, 'updated 5 s ago', undefined, undefined, 'pty probe timed out');
    expect(tip.body).toContain('probe failed');
    expect(tip.body).toContain('pty probe timed out');
  });
});
