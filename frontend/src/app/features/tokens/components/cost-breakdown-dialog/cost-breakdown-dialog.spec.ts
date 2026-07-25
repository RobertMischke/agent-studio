import { signal } from '@angular/core';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { CostBreakdownService } from '../../services/cost-breakdown.service';
import { CostBreakdownDialogComponent } from './cost-breakdown-dialog';

describe('CostBreakdownDialogComponent', () => {
  it('renders rates, counters, formula, source, effective date, and total', async () => {
    const fake = {
      open: signal(true), title: signal('Pipeline cost calculation'), loading: signal(false),
      error: signal<string | null>(null), provider: signal('TokenEconomy'),
      close: () => fake.open.set(false),
      items: signal([{
        model: 'claude-opus-4-7', label: 'Core agent', calculatedAt: '2026-07-11T10:00:00Z',
        inputTokens: 1_000_000, outputTokens: 200_000, cacheReadTokens: 100_000, cacheWriteTokens: 50_000,
        estimate: {
          inputUsd: 5, outputUsd: 5, cacheReadUsd: 0.05, cacheWriteUsd: 0.3125,
          total: 10.3625, modelId: 'claude-opus-4-7', modelKnown: true, status: 'resolved',
          priceBasis: {
            inputPerMillion: 5, outputPerMillion: 25, cacheReadPerMillion: 0.5,
            cacheWritePerMillion: 6.25, currency: 'USD', validFrom: '2026-01-01T00:00:00Z',
            source: 'Anthropic published pricing', note: null, unconfirmed: false,
          },
        },
      }]),
    };
    await TestBed.configureTestingModule({
      imports: [CostBreakdownDialogComponent],
      providers: [provideZonelessChangeDetection(), { provide: CostBreakdownService, useValue: fake }],
    }).compileComponents();
    const fixture = TestBed.createComponent(CostBreakdownDialogComponent);
    fixture.detectChanges();
    const text = document.body.textContent ?? '';
    expect(text).toContain('claude-opus-4-7');
    expect(text).toContain('Input / 1M');
    expect(text).toContain('1,000,000 / 1M × $5.00');
    expect(text).toContain('Anthropic published pricing');
    expect(text).toContain('Price effective date');
    expect(text).toContain('$10.36');
    fixture.destroy();
  });
});
