import { TestBed } from '@angular/core/testing';
import { TaskTestEvidenceComponent } from './task-test-evidence';
import type { TaskTestRunEvidence } from '../../../../models/task.model';

describe('TaskTestEvidenceComponent', () => {
  it.each([
    ['perfect', 'proven', 'Perfect match'],
    ['contains-diff', 'proven', '10 commit(s) after, diff included'],
    ['none', 'unassigned', 'No matching test run'],
  ] as const)('renders %s evidence honestly', async (quality, state, summary) => {
    await TestBed.configureTestingModule({ imports: [TaskTestEvidenceComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TaskTestEvidenceComponent);
    fixture.componentRef.setInput('evidence', {
      runId: quality === 'none' ? null : 'TR-42',
      runCommit: quality === 'none' ? null : 'abcdef12',
      runState: quality === 'none' ? null : 'completed',
      runResult: quality === 'none' ? null : 'passed',
      matchQuality: quality,
      direction: quality === 'perfect' ? 'exact' : quality === 'contains-diff' ? 'after' : 'none',
      distance: quality === 'perfect' ? 0 : quality === 'contains-diff' ? 10 : null,
      diffContained: quality !== 'none',
      evidenceState: state,
      awaitingEvidence: false,
      summary,
    } satisfies TaskTestRunEvidence);
    fixture.detectChanges();

    const evidence = fixture.nativeElement.querySelector('[data-testid="task-card-test-evidence"]') as HTMLElement;
    expect(evidence.textContent).toContain(summary);
    expect(evidence.getAttribute('data-match-quality')).toBe(quality);
    expect(evidence.getAttribute('data-evidence-state')).toBe(state);
    if (quality === 'none') expect(evidence.textContent).toContain('No SHA-linked project run');
  });

  it('names SHA-linked review and gate evidence instead of the unassigned default', async () => {
    await TestBed.configureTestingModule({ imports: [TaskTestEvidenceComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TaskTestEvidenceComponent);
    fixture.componentRef.setInput('evidence', {
      runId: null,
      runCommit: null,
      runState: null,
      runResult: null,
      matchQuality: 'perfect',
      direction: 'exact',
      distance: 0,
      diffContained: true,
      evidenceState: 'proven',
      awaitingEvidence: false,
      summary: 'Review build-tests Pass at d1649ce9',
      sources: [
        {
          kind: 'review-build-tests',
          id: 'review-42',
          commit: 'd1649ce9',
          result: 'passed',
          observedAt: '2026-07-29T20:41:22Z',
          summary: 'Review build-tests Pass at d1649ce9',
        },
        {
          kind: 'build-test-gate',
          id: 'gate-42',
          commit: 'd1649ce9',
          result: 'passed',
          observedAt: '2026-07-29T20:40:00Z',
          summary: 'Build/test gate green at d1649ce9',
        },
      ],
    } satisfies TaskTestRunEvidence);
    fixture.detectChanges();

    const evidence = fixture.nativeElement.querySelector('[data-testid="task-card-test-evidence"]') as HTMLElement;
    expect(evidence.textContent).toContain('Review build-tests Pass at d1649ce9');
    expect(evidence.textContent).toContain('Build/test gate green at d1649ce9');
    expect(evidence.textContent).not.toContain('Evidence pending');
  });
});
