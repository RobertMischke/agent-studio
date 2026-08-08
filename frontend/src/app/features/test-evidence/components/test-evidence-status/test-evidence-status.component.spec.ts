import { TestBed } from '@angular/core/testing';
import type { TaskTestRunEvidence } from '../../../../models/task.model';
import { TestEvidenceStatusComponent } from './test-evidence-status.component';

function evidence(state: TaskTestRunEvidence['evidenceState'], summary: string): TaskTestRunEvidence {
  return {
    runId: null,
    runCommit: null,
    runState: null,
    runResult: null,
    matchQuality: 'perfect',
    direction: 'exact',
    distance: 0,
    diffContained: true,
    evidenceState: state,
    awaitingEvidence: false,
    summary,
    sources: [{
      kind: 'build-test-gate',
      id: `gate-${state}`,
      commit: 'd1649ce9',
      result: state === 'not-applicable' ? 'not-applicable' : 'not-proven',
      observedAt: '2026-08-08T10:00:00Z',
      summary,
    }],
  };
}

describe('TestEvidenceStatusComponent', () => {
  it.each([
    ['not-applicable', 'No build/test commands defined at d1649ce9'],
    ['not-proven', 'Build/test gate skipped at d1649ce9'],
  ] as const)('keeps the %s state explicit', async (state, summary) => {
    await TestBed.configureTestingModule({ imports: [TestEvidenceStatusComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TestEvidenceStatusComponent);
    fixture.componentRef.setInput('evidence', evidence(state, summary));
    fixture.detectChanges();

    const status = fixture.nativeElement.querySelector('[data-testid="task-test-evidence"]') as HTMLElement;
    expect(status.dataset['evidenceState']).toBe(state);
    expect(status.textContent).toContain(summary);
  });
});
