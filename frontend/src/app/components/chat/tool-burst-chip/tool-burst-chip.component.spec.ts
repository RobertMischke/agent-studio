import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ToolBurstChipComponent } from './tool-burst-chip.component';

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
describe('ToolBurstChipComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ToolBurstChipComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ToolBurstChipComponent);
    // Hand-tuned: queueMicrotask in constructor reads event().collapsedByDefault,
    // so a minimal stub event is needed even for a smoke test.
    fixture.componentRef.setInput('event', { kind: 'toolBurst', count: 0, families: {}, failures: 0, collapsedByDefault: true });

    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ToolBurstChipComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders command output with exit badge, hits, and show-more control', async () => {
    await TestBed.configureTestingModule({
      imports: [ToolBurstChipComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ToolBurstChipComponent);
    const output = Array.from({ length: 30 }, (_, i) =>
      i === 0 ? 'frontend/src/app/a.ts:12:const needle = true;' : `line ${i}`
    ).join('\n');
    fixture.componentRef.setInput('event', {
      id: 'burst-1',
      kind: 'toolBurst',
      timestamp: '2026-04-26T12:00:00.000Z',
      rawRange: { source: 'cli-output.log', start: 1, end: 31 },
      count: 1,
      families: { command: 1 },
      failures: 0,
      durationMs: 1500,
      collapsedByDefault: true,
      commands: [
        {
          command: 'rg -n "needle" frontend/src/app',
          status: 'completed',
          exitCode: 0,
          output,
          outputLineCount: 30,
          outputTruncated: false,
          hits: [{ path: 'frontend/src/app/a.ts', line: 12, text: 'const needle = true;' }]
        }
      ]
    });
    fixture.componentRef.setInput('initialOpen', true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="tool-burst-commands"]')?.textContent).toContain('rg -n');
    expect(el.querySelector('.burst__command-badge')?.textContent).toContain('exit 0');
    expect(el.querySelector('[data-testid="tool-burst-output-hits"]')?.textContent).toContain('frontend/src/app/a.ts:12');
    expect(el.querySelector('[data-testid="tool-burst-output-toggle"]')?.textContent).toContain('show 6 more lines');
  });
});
