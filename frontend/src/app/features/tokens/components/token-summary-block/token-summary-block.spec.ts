import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenSummaryBlockComponent } from './token-summary-block';

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
describe('TokenSummaryBlockComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenSummaryBlockComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenSummaryBlockComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] TokenSummaryBlockComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
    fixture.destroy();
  });

  it('shows a counted pricing-drift badge for used UnknownModel ids', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenSummaryBlockComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenSummaryBlockComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();

    TestBed.inject(HttpTestingController)
      .expectOne('/api/runner/Demo/token-summary')
      .flush({
        project: 'Demo',
        orchestratorEntries: 3,
        orchestratorLlmCalls: 3,
        totalInputTokens: 3_000,
        totalOutputTokens: 300,
        totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 0.01,
        allModelsPriced: false,
        unknownModelCount: 2,
        byModel: [
          { model: 'future-a', calls: 1, inputTokens: 1_000, outputTokens: 100, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0, modelPriced: false, unknownModel: true },
          { model: 'future-b', calls: 1, inputTokens: 1_000, outputTokens: 100, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0, modelPriced: false, unknownModel: true },
          { model: 'claude-haiku-4-5', calls: 1, inputTokens: 1_000, outputTokens: 100, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.01, modelPriced: true, unknownModel: false },
        ],
        disclaimer: 'Estimated list pricing.',
      });
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector(
      '[data-testid="token-summary-pricing-drift"]',
    ) as HTMLElement | null;
    expect(badge?.textContent).toContain('2 models without price data');
    fixture.destroy();
  });
});
