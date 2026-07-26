import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { PromptDetail } from '../../../../services/prompt-admin.service';
import { PromptCallTelemetryComponent } from './prompt-call-telemetry.component';

describe('PromptCallTelemetryComponent', () => {
  it('renders call totals, daily history, version split, and cost caveat', async () => {
    await TestBed.configureTestingModule({
      imports: [PromptCallTelemetryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PromptCallTelemetryComponent);
    fixture.componentRef.setInput('detail', {
      calls: {
        totalCalls: 12,
        calls7d: 4,
        lastCalledAt: '2026-07-23T10:00:00Z',
        inputTokens: 2400,
        costUsd: 0.012,
        costUsd7d: 0.004,
        unpricedCalls: 0,
        unpricedCalls7d: 0,
        currentVersionCalls: 4,
        isDead: false,
        daily: [
          { date: '2026-07-22', calls: 2, inputTokens: 400, costUsd: 0.002 },
          { date: '2026-07-23', calls: 4, inputTokens: 800, costUsd: 0.004 },
        ],
        versions: [{
          version: 'abcdef1234567890',
          firstCalledAt: '2026-07-21T10:00:00Z',
          lastCalledAt: '2026-07-23T10:00:00Z',
          calls: 4,
          inputTokens: 800,
          costUsd: 0.004,
          unpricedCalls: 0,
          isCurrent: true,
          models: ['claude-haiku-4-5'],
        }],
      },
      costDisclaimer: 'Theoretical API-equivalent estimate, not an invoice.',
    } as PromptDetail);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="prompt-admin-call-total"]')?.textContent).toContain('12');
    expect(host.querySelector('[data-testid="prompt-admin-version-history"]')?.textContent)
      .toContain('abcdef1234');
    expect(host.textContent).toContain('current');
    expect(host.textContent).toContain('not an invoice');
  });
});
