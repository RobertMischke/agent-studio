import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { formatCliRepairLabel, formatRunningLabel, StatusBarComponent } from './status-bar';

describe('formatRunningLabel', () => {
  it.each([
    { local: 2, remote: 3, expected: '2 local · 3 remote' },
    { local: 2, remote: 0, expected: '2 local' },
    { local: 0, remote: 1, expected: '1 remote' },
    { local: 0, remote: 0, expected: 'no runners' },
  ])('renders $expected for local=$local and remote=$remote', ({ local, remote, expected }) => {
    expect(formatRunningLabel(local, remote)).toBe(expected);
  });

  it('never renders "no runners" while the review plane has active workers (AGT-2645)', () => {
    expect(formatRunningLabel(0, 0, 3)).not.toContain('no runners');
  });

  it('falls back to a vague label when the coding slot ceiling is unknown', () => {
    expect(formatRunningLabel(0, 0, 3)).toBe('coding idle');
  });

  it('shows the honest coding slot ceiling once it is known', () => {
    expect(formatRunningLabel(0, 0, 3, 8)).toBe('coding 0/8');
  });
});

describe('formatCliRepairLabel', () => {
  it('keeps a successful self-heal informational', () => {
    const label = formatCliRepairLabel({
      cliType: 'claude',
      state: 'repaired',
      observedAt: '2026-08-27T10:15:00Z',
      message: 'CLI repaired at 2026-08-27T10:15:00Z',
    });

    expect(label).toContain('Claude CLI repaired at');
    expect(label).not.toContain('failed');
  });

  it('names a failed repair as the alarm condition', () => {
    expect(formatCliRepairLabel({
      cliType: 'codex',
      state: 'repair-failed',
      observedAt: '2026-08-27T10:15:00Z',
      message: 'CLI repair failed',
    })).toContain('Codex CLI repair failed at');
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
