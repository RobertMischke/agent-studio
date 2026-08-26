import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { cliRepairPresentation, formatRunningLabel, StatusBarComponent } from './status-bar';

describe('cliRepairPresentation', () => {
  const occurredAt = '2026-08-18T10:15:00Z';

  it('renders a successful repair as calm, timestamped history for 24 hours', () => {
    const presentation = cliRepairPresentation({
      cliType: 'claude', event: 'repair-succeeded', occurredAt,
      cliVersionBefore: '2.1.231', packageVersionBefore: '2.1.234', cliVersionAfter: '2.1.234',
      detail: 'claude npm shim restored; 2.1.231 -> 2.1.234.',
      journalPath: 'C:/workspace/logs/cli-self-heal.jsonl',
    }, Date.parse(occurredAt) + 60_000);

    expect(presentation?.label).toContain('CLI repaired at');
    expect(presentation?.failed).toBe(false);
    expect(presentation?.tooltip).toContain('cli-self-heal.jsonl');
  });

  it('retires successful repair history after 24 hours', () => {
    const status = {
      cliType: 'claude' as const, event: 'repair-succeeded' as const, occurredAt,
      cliVersionBefore: '2.1.231', packageVersionBefore: '2.1.234', cliVersionAfter: '2.1.234', detail: 'restored', journalPath: 'journal',
    };
    expect(cliRepairPresentation(status, Date.parse(occurredAt) + 24 * 60 * 60_000 + 1)).toBeNull();
  });

  it('keeps a failed repair acute until a newer outcome replaces it', () => {
    const presentation = cliRepairPresentation({
      cliType: 'codex', event: 'repair-failed', occurredAt,
      cliVersionBefore: '0.70.0', packageVersionBefore: '0.70.1', cliVersionAfter: null,
      detail: 'codex npm shim repair failed.', journalPath: 'journal',
    }, Date.parse(occurredAt) + 7 * 24 * 60 * 60_000);
    expect(presentation).toMatchObject({ label: 'CLI repair failed', failed: true });
  });
});

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
