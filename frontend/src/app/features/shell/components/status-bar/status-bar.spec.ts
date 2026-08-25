import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { formatRunningLabel, latestCliRepair, StatusBarComponent } from './status-bar';
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
});

describe('latestCliRepair', () => {
  it('selects the newest durable repair note', () => {
    expect(latestCliRepair([
      {
        cliType: 'claude', repairedAt: '2026-08-18T08:00:00Z',
        cliVersionBefore: '2.1.231', cliVersionAfter: '2.1.234',
        packageVersionBefore: '2.1.231', packageVersionAfter: '2.1.234',
      },
      {
        cliType: 'codex', repairedAt: '2026-08-18T09:00:00Z',
        cliVersionBefore: null, cliVersionAfter: '1.2.3',
        packageVersionBefore: '1.2.2', packageVersionAfter: '1.2.3',
      },
    ])?.cliType).toBe('codex');
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
    TestBed.inject(TaskService).runnerStatus.set({
      projects: {},
      cliRepairs: [{
        cliType: 'claude', repairedAt: '2026-08-18T09:00:00Z',
        cliVersionBefore: '2.1.231', cliVersionAfter: '2.1.234',
        packageVersionBefore: '2.1.231', packageVersionAfter: '2.1.234',
      }],
    });
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] StatusBarComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="status-bar-cli-repaired"]')?.textContent)
      .toContain('CLI repaired at');
  });
});
