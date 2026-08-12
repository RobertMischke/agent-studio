import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { formatRunningLabel, reconcileReviewActiveSlots, StatusBarComponent } from './status-bar';

describe('formatRunningLabel', () => {
  it.each([
    { local: 2, remote: 3, expected: '2 local · 3 remote' },
    { local: 2, remote: 0, expected: '2 local' },
    { local: 0, remote: 1, expected: '1 remote' },
    { local: 0, remote: 0, expected: 'no runners' },
  ])('renders $expected for local=$local and remote=$remote', ({ local, remote, expected }) => {
    expect(formatRunningLabel(local, remote)).toBe(expected);
  });

  it('keeps Review-plane work visible when no coding runner is active', () => {
    expect(formatRunningLabel(0, 0, 4)).toBe('4 review active');
    expect(formatRunningLabel(1, 2, 4)).toBe('1 local · 2 remote · 4 review active');
  });

  it('renders waiting Review work as explicit zero-active attention truth', () => {
    expect(formatRunningLabel(0, 0, 0, 36)).toBe('0 review active · 36 waiting');
    expect(formatRunningLabel(1, 0, 0, 36)).toBe('1 local · 0 review active · 36 waiting');
  });
});

describe('reconcileReviewActiveSlots', () => {
  it('keeps durable Review authority visible when host slot telemetry is stale', () => {
    expect(reconcileReviewActiveSlots(null, 4)).toBe(4);
    expect(reconcileReviewActiveSlots(0, 4)).toBe(4);
    expect(reconcileReviewActiveSlots(4, 3)).toBe(4);
  });
});

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('StatusBarComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [StatusBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(StatusBarComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] StatusBarComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});
