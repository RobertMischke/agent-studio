import { TestBed } from '@angular/core/testing';
import { TokenUsagePopoverComponent } from './token-usage-popover.component';

describe('TokenUsagePopoverComponent', () => {
  it('renders aligned per-type and dated per-run costs with a quiet footnote', async () => {
    await TestBed.configureTestingModule({ imports: [TokenUsagePopoverComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TokenUsagePopoverComponent);
    fixture.componentRef.setInput('bubble', {
      label: '3.3k', total: 3300, input: 3000, output: 300, cacheRead: 0, cacheWrite: 0,
      costLabel: '$0.09', costTooltip: 'Estimated cost: $0.09\nFull dated-price caveat.',
      model: 'GPT-5 Codex', lastUpdate: '8/12/2026, 10:00:00 AM', tier: 'neutral',
      byType: [{
        type: 'coding', label: 'Coding run', calls: 1, total: 3300,
        costLabel: '$0.09', costTooltip: 'Coding cost: $0.09',
      }],
      entries: [{
        id: 'run-1', ts: '2026-08-12T08:00:00Z', tsLabel: '8/12/2026, 10:00:00 AM',
        model: 'GPT-5 Codex', type: 'coding', typeLabel: 'Coding run',
        contextTooltip: 'codex-turn · Run run-1', total: 3300, costUsd: 0.09,
        priceKnown: true, costLabel: '$0.09', costTooltip: 'Price at 8/12/2026: $0.09',
      }],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="token-breakdown-by-type"]')?.textContent).toContain('Coding run');
    expect(root.querySelector('[data-testid="token-breakdown-by-run"]')?.textContent).toContain('$0.09');
    expect(root.querySelector('[data-testid="token-breakdown-by-run"]')?.textContent).toContain('8/12/2026');
    expect(root.querySelector('[data-testid="token-cost-tooltip"]')?.textContent?.trim()).toBe('Dated list-price estimates');
  });
});
