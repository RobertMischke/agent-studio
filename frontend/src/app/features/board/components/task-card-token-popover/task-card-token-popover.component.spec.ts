import { TestBed } from '@angular/core/testing';
import { TaskCardTokenPopoverComponent } from './task-card-token-popover.component';
import type { TaskTokenBubble } from '../task-card/task-card-view-model';

describe('TaskCardTokenPopoverComponent', () => {
  it('renders aligned dated costs and type totals with a quiet footnote', async () => {
    await TestBed.configureTestingModule({ imports: [TaskCardTokenPopoverComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardTokenPopoverComponent);
    fixture.componentRef.setInput('bubble', bubble());
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="token-total-cost"]')?.textContent).toContain('$1.25');
    expect(root.querySelector('[data-testid="token-breakdown-by-type"]')?.textContent).toContain('Coding run');
    expect(root.querySelector('[data-testid="token-breakdown-by-type"]')?.textContent).toContain('Review run');
    expect(root.querySelector('[data-testid="token-breakdown-by-run"]')?.textContent).toContain('2026');
    expect(root.querySelector('[data-testid="token-breakdown-by-run"]')?.textContent).toContain('$0.80');
    expect(root.querySelector('[data-testid="token-cost-footnote"]')?.textContent?.trim())
      .toBe('Dated list-price estimate ⓘ');
    expect(root.textContent).not.toContain('discounts and provider-side caching adjustments');
  });
});

function bubble(): TaskTokenBubble {
  return {
    label: '400k', total: 400_000, input: 120_000, output: 18_000,
    cacheRead: 250_000, cacheWrite: 12_000, costLabel: '$1.25',
    costTooltip: 'Estimated cost: $1.25\nEstimated from historical dated prices.',
    model: 'GPT-5 Codex', lastUpdate: '5/5/2026, 10:30:00 AM', tier: 'blue',
    byType: [
      { id: 'coding', label: 'Coding run', calls: 2, total: 270_000, costLabel: '$0.80', priceKnown: true },
      { id: 'review', label: 'Review run', calls: 1, total: 130_000, costLabel: '$0.45', priceKnown: true },
    ],
    entries: [
      {
        id: 'run-1', ts: '2026-05-05T08:00:00Z', tsLabel: '5/5/2026, 10:00:00 AM',
        model: 'GPT-5 Codex', typeLabel: 'Coding run', contextLabel: 'core agent run',
        contextTooltip: 'core-agent-run · run-1', total: 160_000, costLabel: '$0.80', priceKnown: true,
      },
    ],
  };
}
