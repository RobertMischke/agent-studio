import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { CliWindowAnalysisComponent } from './cli-window-analysis';

describe('CliWindowAnalysisComponent', () => {
  it('shows the contributing model entries as the recorded attribution period', async () => {
    await TestBed.configureTestingModule({
      imports: [CliWindowAnalysisComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliWindowAnalysisComponent);
    fixture.componentRef.setInput('cliType', 'codex');
    fixture.componentRef.setInput('tokens', {
      byModel: [{
        model: 'gpt-5-codex', calls: 2, inputTokens: 100, outputTokens: 20,
        cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0,
        modelPriced: false, firstRecordedAt: '2026-07-11T08:15:00Z',
        lastRecordedAt: '2026-08-11T09:05:00Z',
      }],
    } as never);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="cli-window-recording-period"]')?.textContent)
      .toContain('Since 11 Jul 2026 · as of 11 Aug 2026, 09:05 UTC');
  });
});
