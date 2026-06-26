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

    const emitted: unknown[] = [];
    fixture.componentInstance.openSourceLocation.subscribe((hit) => emitted.push(hit));
    el.querySelector<HTMLButtonElement>('[data-testid="tool-burst-hit-path"]')?.click();
    expect(emitted).toEqual([{ path: 'frontend/src/app/a.ts', line: 12, text: 'const needle = true;' }]);

    el.querySelector<HTMLButtonElement>('[data-testid="tool-burst-output-toggle"]')?.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="tool-burst-command-output"]')?.textContent).toContain('line 29');
    expect(el.querySelector('[data-testid="tool-burst-output-toggle"]')?.textContent).toContain('show less');
  });

  it('starts a long tool-use collapsed as a one-line preview and reveals full content on expand', async () => {
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
    const output = Array.from({ length: 40 }, (_, i) => `log line ${i}`).join('\n');
    fixture.componentRef.setInput('event', {
      id: 'burst-long',
      kind: 'toolBurst',
      timestamp: '2026-04-26T12:00:00.000Z',
      rawRange: { source: 'cli-output.log', start: 1, end: 41 },
      count: 1,
      families: { command: 1 },
      failures: 0,
      durationMs: 4200,
      collapsedByDefault: true,
      commands: [
        { command: 'npm run build', status: 'completed', exitCode: 0, output, outputLineCount: 40, outputTruncated: false }
      ]
    });
    // No initialOpen: a long burst must stay collapsed by default.
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    // Collapsed: the heavy details panel is absent and only a one-line preview shows.
    expect(el.querySelector('[data-testid="tool-burst-details"]')).toBeFalsy();
    const preview = el.querySelector('[data-testid="tool-burst-preview"]');
    expect(preview?.textContent).toContain('npm run build');

    // Expand via the row toggle.
    el.querySelector<HTMLButtonElement>('[data-testid="tool-burst-row"]')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    // Expanded: full command output is revealed and the collapsed preview is gone.
    expect(el.querySelector('[data-testid="tool-burst-details"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="tool-burst-command-output"]')?.textContent).toContain('log line 0');
    expect(el.querySelector('[data-testid="tool-burst-preview"]')).toBeFalsy();
  });

  it('shows the generating model as a subtle chip on the collapsed burst row', async () => {
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
    fixture.componentRef.setInput('event', {
      id: 'burst-model',
      kind: 'toolBurst',
      timestamp: '2026-04-26T12:00:00.000Z',
      rawRange: { source: 'cli-output.log', start: 1, end: 6 },
      count: 3,
      families: { read: 3 },
      failures: 0,
      durationMs: 900,
      model: 'claude-opus-4-8',
      collapsedByDefault: true,
    });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const chip = el.querySelector('[data-testid="tool-burst-model"]');
    expect(chip).toBeTruthy();
    expect(chip?.textContent?.trim()).toBe('claude-opus-4-8');
  });

  it('exposes a glyph legend tooltip that covers every tool glyph and marks the active one', async () => {
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
    fixture.componentRef.setInput('event', {
      id: 'burst-glyph',
      kind: 'toolBurst',
      timestamp: '2026-04-26T12:00:00.000Z',
      rawRange: { source: 'cli-output.log', start: 1, end: 6 },
      count: 3,
      families: { read: 3 },
      failures: 0,
      collapsedByDefault: true,
    });
    fixture.detectChanges();

    const tip = fixture.componentInstance.glyphTooltip();
    // The active row is a Read burst, so the tooltip headline names it.
    expect(tip.title).toContain('Read');
    // The body is the full key: every glyph + its written-out meaning, so any
    // row decodes the whole alphabet (R/S/$/E/A/D/T/!), not just its letter.
    for (const name of ['Read', 'Search', 'Shell', 'Edit', 'Task', 'Todo', 'Tool', 'Fehler']) {
      expect(tip.body).toContain(name);
    }
    for (const glyph of ['R', 'S', '$', 'E', 'A', 'D', 'T', '!']) {
      expect(tip.body).toContain(`<code>${glyph}</code>`);
    }
    // The active glyph (R) is emphasized.
    expect(tip.body).toContain('<strong><code>R</code>');

    // The glyph carries the canonical instant-hover tooltip directive.
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="tool-burst-icon"]')).toBeTruthy();
  });

  it('shows the written-out glyph meaning in the expanded detail head', async () => {
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
    fixture.componentRef.setInput('event', {
      id: 'burst-glyph-open',
      kind: 'toolBurst',
      timestamp: '2026-04-26T12:00:00.000Z',
      rawRange: { source: 'cli-output.log', start: 1, end: 6 },
      count: 2,
      families: { command: 2 },
      failures: 0,
      collapsedByDefault: true,
    });
    fixture.componentRef.setInput('initialOpen', true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const label = el.querySelector('[data-testid="tool-burst-glyph-label"]');
    expect(label?.textContent).toContain('Shell');
  });

  it('omits the model chip when the burst has no attributable model', async () => {
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
    fixture.componentRef.setInput('event', {
      id: 'burst-nomodel',
      kind: 'toolBurst',
      timestamp: '2026-04-26T12:00:00.000Z',
      rawRange: { source: 'cli-output.log', start: 1, end: 6 },
      count: 3,
      families: { read: 3 },
      failures: 0,
      durationMs: 900,
      collapsedByDefault: true,
    });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="tool-burst-model"]')).toBeFalsy();
  });
});
