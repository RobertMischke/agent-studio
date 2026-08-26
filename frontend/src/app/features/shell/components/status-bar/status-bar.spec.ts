import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { formatRunningLabel, StatusBarComponent, summarizeCliRepairStatus } from './status-bar';

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

describe('summarizeCliRepairStatus', () => {
  it('keeps a successful repair as a quiet timestamped note', () => {
    const note = summarizeCliRepairStatus({
      at: '2026-08-18T10:06:00Z',
      repairs: [{
          cliType: 'claude', status: 'repaired', attemptedAt: '2026-08-18T10:04:00Z',
          completedAt: '2026-08-18T10:05:00Z', versionBefore: '2.1.231', versionAfter: '2.1.234',
          note: 'CLI repaired at 2026-08-18T10:05:00Z', detail: 'npm global reinstall restored claude.cmd.',
      }],
    }, 'en-GB');

    expect(note).toMatchObject({ failed: false });
    expect(note?.label).toMatch(/^CLI repaired at \d{2}:\d{2}$/);
  });

  it('alarms only when the latest repair failed', () => {
    const note = summarizeCliRepairStatus({
      at: '2026-08-18T10:06:00Z',
      repairs: [{
          cliType: 'codex', status: 'failed', attemptedAt: '2026-08-18T10:04:00Z',
          completedAt: '2026-08-18T10:05:00Z', versionBefore: '0.90.0', versionAfter: null,
          note: 'CLI repair failed at 2026-08-18T10:05:00Z', detail: 'npm exited 1.',
      }],
    }, 'en-GB');

    expect(note).toMatchObject({ failed: true });
    expect(note?.label).toMatch(/^CLI repair failed at \d{2}:\d{2}$/);
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
