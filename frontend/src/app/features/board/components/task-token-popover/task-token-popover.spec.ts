import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { TaskTokenPopoverComponent } from './task-token-popover';

describe('TaskTokenPopoverComponent', () => {
  it('renders aligned type and dated run cost tables with a quiet footnote', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskTokenPopoverComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskTokenPopoverComponent);
    fixture.componentRef.setInput('value', {
      label: '12k', total: 12_000, input: 8_000, output: 1_000,
      cacheRead: 3_000, cacheWrite: 0, costLabel: '$0.12',
      disclaimer: 'Each event uses its dated catalog rate.', model: 'GPT-5 Codex',
      lastUpdate: 'Aug 12, 2026', tier: 'neutral',
      byType: [{ type: 'coding-run', label: 'Coding run', calls: 1, total: 12_000, costLabel: '$0.12' }],
      runs: [{ id: 'run-1', ts: '2026-08-12T08:00:00Z', tsLabel: 'Aug 12, 2026, 10:00', typeLabel: 'Coding run', model: 'GPT-5 Codex', calls: 1, total: 12_000, costLabel: '$0.12' }],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="token-type-table"]')?.textContent).toContain('Coding run');
    expect(root.querySelector('[data-testid="token-run-table"]')?.textContent).toContain('Aug 12, 2026');
    expect(root.querySelector('[data-testid="token-run-table"]')?.textContent).toContain('$0.12');
    expect(root.querySelector('[data-testid="token-pricing-footnote"]')?.textContent)
      .toContain('rate valid on each event date');
  });
});
