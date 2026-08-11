import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliWindowAnalysisComponent } from './cli-window-analysis';

describe('CliWindowAnalysisComponent', () => {
  it('shows exact provider and recorded-telemetry timestamps', async () => {
    await TestBed.configureTestingModule({
      imports: [CliWindowAnalysisComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliWindowAnalysisComponent);
    fixture.componentRef.setInput('cliType', 'codex');
    fixture.componentRef.setInput('quotaRows', [{
      cliType: 'codex', fetchedAt: '2026-08-11T20:43:00Z', windows: [],
    }] as never);
    fixture.componentRef.setInput('tokens', {
      byModel: [{
        model: 'gpt-5-codex', inputTokens: 100, outputTokens: 20,
        cacheReadTokens: 0, cacheCreationTokens: 0,
        firstActivity: '2026-07-11T08:15:00Z', lastActivity: '2026-08-11T19:42:18Z',
      }],
    } as never);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="cli-window-quota-as-of"]')?.textContent)
      .toContain('Provider snapshot as of 2026-08-11 20:43 UTC');
    expect(host.querySelector('[data-testid="cli-window-recorded-period"]')?.textContent)
      .toContain('Recorded since 2026-07-11 · as of 2026-08-11 19:42 UTC');
  });
});
