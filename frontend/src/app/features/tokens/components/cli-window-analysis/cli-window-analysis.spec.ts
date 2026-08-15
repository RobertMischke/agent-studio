import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { TokenSummaryAggregate } from '../../models/tokens.model';
import { CliWindowAnalysisComponent } from './cli-window-analysis';

describe('CliWindowAnalysisComponent', () => {
  it('derives recorded and quota timestamps from telemetry inputs', async () => {
    await TestBed.configureTestingModule({
      imports: [CliWindowAnalysisComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliWindowAnalysisComponent);
    fixture.componentRef.setInput('cliType', 'codex');
    fixture.componentRef.setInput('quotaRows', [quotaRow()]);
    fixture.componentRef.setInput('tokens', tokenAggregate());
    fixture.detectChanges();

    expect(fixture.componentInstance.quotaAsOf()).toBe('2026-08-11T12:45:00Z');
    expect(fixture.componentInstance.recordedUsagePeriod()).toEqual({
      oldestRecordedAt: '2026-06-01T08:15:00Z',
      newestRecordedAt: '2026-08-11T12:30:00Z',
    });
    expect(fixture.nativeElement.querySelector('[data-testid="cli-window-recorded-period"]')?.textContent)
      .toContain('Since 01 Jun 2026 · as of 11 Aug 2026, 12:30 UTC');
    expect(fixture.nativeElement.querySelector('[data-testid="cli-window-quota-as-of"]')?.textContent)
      .toContain('As of 11 Aug 2026, 12:45 UTC');
  });
});

function quotaRow(): CliUsageQuotaRow {
  return {
    cliType: 'codex', icon: '', label: 'Codex', plan: 'Pro', fetchedAt: '2026-08-11T12:45:00Z',
    freshness: 'updated 1 min ago', stale: false, source: '/status', error: null, windows: [],
    primary: null, primaryPct: null, primaryTone: 'unknown',
  };
}

function tokenAggregate(): TokenSummaryAggregate {
  return {
    projects: 1, orchestratorEntries: 2, orchestratorLlmCalls: 2,
    totalInputTokens: 300, totalOutputTokens: 30, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false,
    byModel: [
      {
        model: 'gpt-5-codex', calls: 1, inputTokens: 100, outputTokens: 10,
        cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0, modelPriced: false,
        oldestRecordedAt: '2026-06-01T08:15:00Z', newestRecordedAt: '2026-08-10T11:45:00Z',
      },
      {
        model: 'gpt-5.5', calls: 1, inputTokens: 200, outputTokens: 20,
        cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0, modelPriced: false,
        oldestRecordedAt: '2026-06-14T09:30:00Z', newestRecordedAt: '2026-08-11T12:30:00Z',
      },
    ],
    byProject: [], fetchedAt: '2026-08-11T12:45:00Z', disclaimer: '',
  };
}
