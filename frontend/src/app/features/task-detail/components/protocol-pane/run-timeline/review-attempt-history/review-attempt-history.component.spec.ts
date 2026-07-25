import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { ReviewAttemptCycle } from '../../../../../../features/run-timeline';
import { ReviewAttemptHistoryComponent } from './review-attempt-history.component';

describe('ReviewAttemptHistoryComponent', () => {
  it('shows the current epoch and preserves closed operator cycles', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewAttemptHistoryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(ReviewAttemptHistoryComponent);
    fixture.componentRef.setInput('currentEpoch', 2);
    fixture.componentRef.setInput('cycles', cycles());
    fixture.detectChanges();

    const current = fixture.nativeElement.querySelector('[data-testid="review-attempt-current"]');
    expect(current.textContent).toContain('Epoch 2');

    const currentCycle = fixture.nativeElement.querySelector('[data-testid="review-attempt-cycle-2"]');
    expect(currentCycle.textContent).toContain('Current');
    expect(currentCycle.textContent).toContain('Runner recovered; assess fresh evidence.');
    expect(currentCycle.textContent).toContain('2 artifacts archived');

    const previous = fixture.nativeElement.querySelector('[data-testid="review-attempt-cycle-1"]');
    expect(previous.textContent).toContain('Closed');
    expect(previous.textContent).toContain('5e-escalated → 4-auto-review');
    expect(previous.textContent).toContain('Infrastructure repaired.');

    expect(fixture.nativeElement.querySelectorAll('[data-testid^="review-attempt-cycle-"]')).toHaveLength(3);
  });

  it('renders legacy tasks as epoch zero without requiring history data', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewAttemptHistoryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(ReviewAttemptHistoryComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Epoch 0');
    expect(fixture.nativeElement.textContent).toContain('Initial review cycle.');
  });
});

function cycles(): ReviewAttemptCycle[] {
  return [
    {
      epoch: 2,
      isCurrent: true,
      startedAt: '2026-07-23T15:45:00Z',
      endedAt: null,
      actor: 'human:operator@example.com',
      reason: 'Runner recovered; assess fresh evidence.',
      fromState: '5-human-review',
      toState: '4-auto-review',
      rotatedArtifacts: 2,
    },
    {
      epoch: 1,
      isCurrent: false,
      startedAt: '2026-07-23T06:30:00Z',
      endedAt: '2026-07-23T15:45:00Z',
      actor: 'human:operator@example.com',
      reason: 'Infrastructure repaired.',
      fromState: '5e-escalated',
      toState: '4-auto-review',
      rotatedArtifacts: 4,
    },
    {
      epoch: 0,
      isCurrent: false,
      startedAt: '2026-07-22T21:00:00Z',
      endedAt: '2026-07-23T06:30:00Z',
      actor: null,
      reason: 'Initial review cycle.',
      fromState: null,
      toState: null,
      rotatedArtifacts: 0,
    },
  ];
}
