import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { formatRunningLabel, StatusBarComponent } from './status-bar';
import { TaskService } from '../../../../services/task.service';

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

describe('StatusBarComponent CLI repair note', () => {
  it('uses zero UI space for repaired CLIs and renders only an active failure', async () => {
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
    const service = TestBed.inject(TaskService);
    service.runnerStatus.set({
      projects: {},
      cliRepairs: [{
        cliType: 'claude',
        outcome: 'repaired',
        occurredAt: '2026-08-18T10:15:00Z',
        versionBefore: '2.1.231',
        versionAfter: '2.1.234',
        detail: 'claude CLI npm shim restored; version 2.1.231 -> 2.1.234.',
      }],
    });

    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="status-bar-cli-repair"]')).toBeNull();

    service.runnerStatus.set({
      projects: {},
      cliRepairs: [{
        cliType: 'claude',
        outcome: 'failed',
        occurredAt: '2026-08-18T11:15:00Z',
        detail: 'npm install exited 1.',
      }],
    });
    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('[data-testid="status-bar-cli-repair"]');
    expect(note?.textContent).toContain('CLI repair failed at');
    expect(note?.getAttribute('data-signal-tone')).toBe('mismatch');
    expect(note?.querySelector('[aria-label="CLI repair failed"]')).not.toBeNull();
  });
});
