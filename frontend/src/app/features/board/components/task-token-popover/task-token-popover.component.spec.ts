import { TestBed } from '@angular/core/testing';
import { TaskTokenPopoverComponent } from './task-token-popover.component';
import type { TaskTokenBubble } from '../task-card/task-card-view-model';

describe('TaskTokenPopoverComponent', () => {
  it('renders aligned type and dated run cost rows', async () => {
    await TestBed.configureTestingModule({ imports: [TaskTokenPopoverComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TaskTokenPopoverComponent);
    const bubble: TaskTokenBubble = {
      label: '3.3k', total: 3300, input: 3000, output: 300, cacheRead: 0, cacheWrite: 0,
      costTooltip: 'Estimated cost: $0.09', costLabel: '$0.09', costIncomplete: false,
      costNotice: 'Each run uses the price valid on its recorded date.', model: 'GPT-5 Codex',
      lastUpdate: 'Aug 12, 10:00', tier: 'neutral',
      byType: [{ usageType: 'coding', label: 'Coding run', calls: 1, total: 2200, costUsd: 0.06, costLabel: '$0.06', priceKnown: true }],
      entries: [{
        id: 'evt-1', ts: '2026-08-12T08:00:00Z', tsLabel: 'Aug 12, 10:00', model: 'GPT-5 Codex',
        usageType: 'coding', typeLabel: 'Coding run', total: 2200, costUsd: 0.06,
        costLabel: '$0.06', priceKnown: true, contextTooltip: 'agent-run · Catalog price effective Aug 1, 2026',
      }],
    };
    fixture.componentRef.setInput('bubble', bubble);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="token-breakdown-by-type"]')?.textContent).toContain('Coding run');
    expect(root.querySelector('[data-testid="token-breakdown-by-run"]')?.textContent).toContain('Aug 12, 10:00');
    expect(root.querySelector('[data-testid="token-breakdown-by-run"]')?.textContent).toContain('$0.06');
    expect(root.querySelector('[data-testid="token-cost-footnote"]')?.textContent).toContain('Estimated at each run date');
  });
});
