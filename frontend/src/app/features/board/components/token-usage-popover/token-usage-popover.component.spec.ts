import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskTokenBubble } from '../task-card/task-card-view-model';
import { TaskTokenUsagePopoverComponent } from './token-usage-popover.component';

describe('TaskTokenUsagePopoverComponent', () => {
  it('renders reconciling type and dated-run cost columns', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskTokenUsagePopoverComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskTokenUsagePopoverComponent);
    const usage: TaskTokenBubble = {
      label: '3k', total: 3000, input: 2500, output: 500,
      cacheRead: 0, cacheWrite: 0, calls: 2,
      costLabel: '$0.12', costTooltip: 'Full historical pricing detail',
      model: 'Claude Sonnet 5', lastUpdate: '8/12/2026, 10:00:00 AM', tier: 'neutral',
      typeGroups: [
        { id: 'coding-run', label: 'Coding run', calls: 1, total: 2000, costLabel: '$0.08' },
        { id: 'review-run', label: 'Review run', calls: 1, total: 1000, costLabel: '$0.04' },
      ],
      entries: [
        { id: 'run-1', ts: '2026-08-12T08:00:00Z', tsLabel: '8/12/2026, 10:00:00 AM', model: 'Claude Sonnet 5', typeLabel: 'Coding run', source: 'claude-turn', total: 2000, costLabel: '$0.08' },
        { id: 'run-2', ts: '2026-08-12T08:05:00Z', tsLabel: '8/12/2026, 10:05:00 AM', model: 'Claude Haiku 4.5', typeLabel: 'Review run', source: 'code-review-step', total: 1000, costLabel: '$0.04' },
      ],
    };
    fixture.componentRef.setInput('usage', usage);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="token-cost-total"]')?.textContent).toContain('$0.12');
    expect(root.querySelector('[data-usage-type="coding-run"]')?.textContent).toContain('$0.08');
    expect(root.querySelector('[data-usage-type="review-run"]')?.textContent).toContain('$0.04');
    expect(root.querySelectorAll('.token-popover__table--runs tbody tr')).toHaveLength(2);
    expect(root.querySelectorAll('[data-testid="token-run-source"]')[1]?.textContent)
      .toContain('code-review-step');
  });
});
