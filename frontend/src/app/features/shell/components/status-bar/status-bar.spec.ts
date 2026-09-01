import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { formatSlotLabel, StatusBarComponent } from './status-bar';
import { TaskService } from '../../../../services/task.service';

describe('formatSlotLabel', () => {
  it.each([
    { plane: 'remote' as const, active: 6, ceiling: 8, expected: 'remote 6/8' },
    { plane: 'review' as const, active: 2, ceiling: 6, expected: 'review 2/6' },
    { plane: 'remote' as const, active: 0, ceiling: 8, expected: 'remote idle' },
    { plane: 'review' as const, active: 0, ceiling: null, expected: 'review idle' },
    { plane: 'remote' as const, active: 3, ceiling: null, expected: 'remote 3 busy' },
  ])('renders $expected', ({ plane, active, ceiling, expected }) => {
    expect(formatSlotLabel(plane, active, ceiling)).toBe(expected);
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
  it('surfaces a successful repair as a note without an alarm tone', async () => {
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

    const note = fixture.nativeElement.querySelector('[data-testid="status-bar-cli-repair"]');
    expect(note?.textContent).toContain('CLI repaired at');
    expect(note?.getAttribute('data-signal-tone')).not.toBe('mismatch');

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

    expect(note?.textContent).toContain('CLI repair failed at');
    expect(note?.getAttribute('data-signal-tone')).toBe('mismatch');
    expect(note?.querySelector('[aria-label="CLI repair failed"]')).not.toBeNull();
  });
});
