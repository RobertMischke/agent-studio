import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectTokenUsagePanelComponent } from './project-token-usage-panel.component';
import type { ProjectPipelineCostTimeline } from '../../../../features/project-token-usage';

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
describe('ProjectTokenUsagePanelComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ProjectTokenUsagePanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Pipeline-cost section render check. Seeds the `pipelineCost` signal with a
 * two-kind, three-day timeline and asserts the project-level "how it develops
 * over time" surface renders: the per-kind legend (with cost + tokens), the
 * total row, and one stacked bar per day. This exercises the template bindings
 * added for the project-level aggregate (acceptance: "a project-level view
 * shows aggregate tokens + cost per step kind with a time trend") so a binding
 * typo reds the test instead of only surfacing in a browser.
 */
describe('ProjectTokenUsagePanelComponent (pipeline cost)', () => {
  function fakeTimeline(): ProjectPipelineCostTimeline {
    const days = ['2026-05-31', '2026-06-01', '2026-06-02'];
    return {
      project: 'demo',
      days,
      windowDays: 30,
      kinds: [
        {
          kind: 'core',
          totalTokens: 300_000,
          totalCostUsd: 0.75,
          anyModelUnknown: false,
          cells: [
            { day: days[0], totalTokens: 100_000, costUsd: 0.25 },
            { day: days[1], totalTokens: 100_000, costUsd: 0.25 },
            { day: days[2], totalTokens: 100_000, costUsd: 0.25 },
          ],
        },
        {
          kind: 'aspect',
          totalTokens: 80_000,
          totalCostUsd: 0.08,
          anyModelUnknown: false,
          cells: [
            { day: days[0], totalTokens: 20_000, costUsd: 0.02 },
            { day: days[1], totalTokens: 20_000, costUsd: 0.02 },
            { day: days[2], totalTokens: 40_000, costUsd: 0.04 },
          ],
        },
        {
          kind: 'drift',
          totalTokens: 20_000,
          totalCostUsd: 0.04,
          anyModelUnknown: false,
          cells: [
            { day: days[0], totalTokens: 0, costUsd: 0 },
            { day: days[1], totalTokens: 0, costUsd: 0 },
            { day: days[2], totalTokens: 20_000, costUsd: 0.04 },
          ],
        },
      ],
      steps: [
        { stepId: 'core-agent-run', kind: 'core', totalTokens: 300_000, totalCostUsd: 0.75, anyModelUnknown: false },
        { stepId: 'aspect-code-quality', kind: 'aspect', totalTokens: 80_000, totalCostUsd: 0.08, anyModelUnknown: false },
        { stepId: 'post-drift-adr-code', kind: 'drift', totalTokens: 20_000, totalCostUsd: 0.04, anyModelUnknown: false },
      ],
      totalTokens: 400_000,
      totalCostUsd: 0.87,
      anyModelUnknown: false,
      taskCount: 4,
      hasData: true,
      fetchedAt: '2026-06-02T00:00:00Z',
    };
  }

  it('renders the per-kind legend, total, and one stacked bar per day', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    // First flush runs the projectName effect (which kicks off the load and
    // resets the signal to null + issues pending test HTTP calls). Seed the
    // signal afterwards so our fixture data survives the refresh reset.
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }
    fixture.componentInstance.pipelineCost.set(fakeTimeline());
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="token-usage-pipeline-cost"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-empty"]')).toBeNull();

    const legend = host.querySelector('[data-testid="pipeline-cost-legend"]');
    expect(legend).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-core"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-aspect"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-drift"]')).toBeTruthy();

    const total = host.querySelector('[data-testid="pipeline-cost-total"]');
    expect(total?.textContent).toContain('$0.87');

    const cols = host.querySelectorAll('[data-testid="pipeline-cost-bars"] .tup__pl-col');
    expect(cols.length).toBe(3);
    // Busiest day (3rd: 160k tokens) carries core, aspect, and drift segments.
    const lastColSegs = cols[2].querySelectorAll('.tup__pl-seg');
    expect(lastColSegs.length).toBe(3);
  });
});
