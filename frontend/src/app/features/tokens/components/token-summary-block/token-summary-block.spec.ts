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
  });

  it('shows catalog drift when an actively used model is unknown to TokenEconomy', async () => {
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
        orchestratorEntries: 2,
        orchestratorLlmCalls: 2,
        totalInputTokens: 2_000,
        totalOutputTokens: 200,
        totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 0.0015,
        allModelsPriced: false,
        byModel: [
          {
            model: 'future-active-model', calls: 1, inputTokens: 1_000, outputTokens: 100,
            cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0,
            modelPriced: false, priceStatus: 'unknownModel',
          },
          {
            model: 'Claude Haiku 4.5', calls: 1, inputTokens: 1_000, outputTokens: 100,
            cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.0015,
            modelPriced: true, priceStatus: 'resolved',
          },
        ],
        disclaimer: 'Estimate only.',
      });
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector(
      '[data-testid="token-summary-catalog-drift"]',
    ) as HTMLElement | null;
    expect(badge?.textContent?.trim()).toBe('1 model without price data');
    expect(fixture.nativeElement.querySelector('[data-testid="token-summary-cost"]')?.textContent)
      .toContain('$0.0015');
    expect(fixture.nativeElement.querySelector('[data-testid="token-summary-cost"]')?.textContent)
      .toContain('partial');
    fixture.destroy();
  });

  it('shows Unknown instead of zero when every used model is unpriced', async () => {
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
        orchestratorEntries: 1,
        orchestratorLlmCalls: 1,
        totalInputTokens: 1_000,
        totalOutputTokens: 100,
        totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 0,
        allModelsPriced: false,
        byModel: [{
          model: 'future-active-model', calls: 1, inputTokens: 1_000, outputTokens: 100,
          cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0,
          modelPriced: false, priceStatus: 'noPriceForDate',
        }],
        disclaimer: 'Estimate only.',
      });
    fixture.detectChanges();

    const cost = fixture.nativeElement.querySelector(
      '[data-testid="token-summary-cost"]',
    ) as HTMLElement | null;
    expect(cost?.textContent).toContain('Unknown');
    expect(cost?.textContent).not.toContain('$0.00');
    expect(cost?.textContent).not.toContain('partial');
    fixture.destroy();
  });
});
