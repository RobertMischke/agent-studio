import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { CliUsageStore } from '../../../tokens';
import { LoadDistributionComponent } from './load-distribution.component';

function mockStore() {
  return {
    ensureQuotaStarted: vi.fn(), startDetail: vi.fn(), stopDetail: vi.fn(),
    quotaRows: signal([]), tokens: signal(null), timeline24h: signal(null), timeline7d: signal(null),
  };
}

describe('LoadDistributionComponent', () => {
  it('groups captured token events by model and reasoning effort without inventing attribution', async () => {
    const store = mockStore();
    await TestBed.configureTestingModule({
      imports: [LoadDistributionComponent],
      providers: [provideZonelessChangeDetection(), { provide: CliUsageStore, useValue: store }],
    }).compileComponents();
    const fixture = TestBed.createComponent(LoadDistributionComponent);
    const ts = new Date(Date.now() - 15 * 60_000).toISOString();
    fixture.componentRef.setInput('entries', [
      { ts, kind: 'decision', topic: 'budget/switch', summary: 'Switch model', tokenUsage: { model: 'gpt-5.6', thinkingLevel: 'high', inputTokens: 100, outputTokens: 20, cacheReadTokens: 30, cacheCreationTokens: 0 } },
      { ts, kind: 'action', topic: 'run', summary: 'Call', tokenUsage: { model: 'gpt-5.6', inputTokens: 40, outputTokens: 10, cacheReadTokens: 0, cacheCreationTokens: 0 } },
    ]);
    fixture.detectChanges();

    const row = fixture.componentInstance.modelRows()[0];
    expect(row.tokens).toBe(200);
    expect(row.effort.map(item => item.level)).toEqual(['high', 'unattributed']);
    expect(fixture.componentInstance.decisions()).toHaveLength(1);
    expect(store.startDetail).toHaveBeenCalledOnce();
    fixture.destroy();
    expect(store.stopDetail).toHaveBeenCalledOnce();
  });

  it('projects a five-hour window from elapsed time to reset', async () => {
    const store = mockStore();
    await TestBed.configureTestingModule({
      imports: [LoadDistributionComponent],
      providers: [provideZonelessChangeDetection(), { provide: CliUsageStore, useValue: store }],
    }).compileComponents();
    const component = TestBed.createComponent(LoadDistributionComponent).componentInstance;
    const projected = component.projection({
      cliType: 'codex', icon: '', label: 'Codex', plan: null, fetchedAt: null, freshness: '', stale: false,
      source: null, error: null, primary: null, primaryPct: 20, primaryTone: 'ok',
      windows: [{ label: '5h', usedPct: 20, used: null, limit: null, unit: null, resetAt: new Date(Date.now() + 2.5 * 3_600_000).toISOString(), resetLabel: 'in 2h 30m' }],
    }, '5h');
    expect(projected).toBeCloseTo(40, 0);
  });
});
