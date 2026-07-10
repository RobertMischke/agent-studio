import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of, throwError } from 'rxjs';
import { EscalationSummaryComponent } from './escalation-summary.component';
import { TaskService } from '../../../../services/task.service';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import type { TaskDetail } from '../../../../models/task.model';
import type { TaskTimelineEvent } from '../../../task-timeline';

function detail(): TaskDetail {
  return {
    info: {
      id: 'AGT-1994',
      watchPath: '/ws',
      state: '5e-escalated',
      orchestratorVerdict: 'escalate',
      commits: [
        { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'm', filesChanged: 4, files: ['a', 'b', 'c', 'd'], at: '' },
      ],
      mergeSignal: {
        branch: 'task/AGT-1994',
        inIntegration: true,
        inRelease: true,
        integrationBranch: 'develop',
        releaseBranch: 'main',
        integrationSha: 'b2ed3f4',
        releaseSha: '1a526e9',
      },
    },
    reviewEvidence: [],
  } as unknown as TaskDetail;
}

function mount(opts: {
  followUp?: string | null;
  reviews?: unknown[];
  events?: TaskTimelineEvent[];
}) {
  const taskStub = {
    listCodeReviews: () => of({ entries: opts.reviews ?? [] }),
    readJobFile: () =>
      opts.followUp == null ? throwError(() => ({ status: 404 })) : of(opts.followUp),
  };
  const timelineStub = { events: signal<TaskTimelineEvent[]>(opts.events ?? []) };

  TestBed.configureTestingModule({
    imports: [EscalationSummaryComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: taskStub },
      { provide: TaskTimelinePollService, useValue: timelineStub },
    ],
  });
  const fixture = TestBed.createComponent(EscalationSummaryComponent);
  fixture.componentRef.setInput('detail', detail());
  fixture.detectChanges();
  return fixture;
}

describe('EscalationSummaryComponent', () => {
  it('renders the review grade, follow-up gate checklist, delivery context and recommendation', () => {
    const fixture = mount({
      followUp: '- [ ] Frontend Playwright verification skipped.\n- [ ] Live Haiku probe not run.',
      reviews: [
        {
          fileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
          verdict: 'pass',
          grade: 'B',
          summary: 'Solid first slice, small gaps.',
          model: 'claude-opus-4-8',
          cliType: 'claude',
          runAt: '2026-07-09T19:22:02Z',
        },
      ],
    });
    const el: HTMLElement = fixture.nativeElement;

    // Review verdict head
    expect(el.querySelector('[data-testid="escalation-review-grade"]')?.textContent?.trim()).toBe('B');
    expect(el.querySelector('[data-testid="escalation-review-verdict"]')?.textContent?.trim()).toBe('pass');
    expect(el.querySelector('[data-testid="escalation-review-summary"]')?.textContent).toContain('Solid first slice');

    // Gate checklist from the follow-up file
    const gateItems = el.querySelectorAll('[data-testid="escalation-gate-items"] li');
    expect(gateItems.length).toBe(2);
    expect(el.querySelector('[data-testid="escalation-gate-source"]')?.textContent).toContain('follow-up checklist');
    expect(el.querySelector('[data-testid="escalation-gate-count"]')?.textContent).toContain('2 open');

    // Delivery context
    expect(el.querySelector('[data-testid="escalation-delivery-counts"]')?.textContent).toContain('1 commit');
    expect(el.querySelector('[data-testid="escalation-delivery-counts"]')?.textContent).toContain('4 files');

    // Recommendation
    expect(el.querySelector('[data-testid="escalation-recommendation"]')?.textContent?.trim()).toBe('Needs decision');
  });

  it('falls back to the escalate timeline findings when no follow-up file exists', () => {
    const fixture = mount({
      followUp: null,
      reviews: [],
      events: [
        {
          ts: '2026-07-09T19:00:00Z',
          kind: 'orchestrator_escalated',
          actor: 'orchestrator',
          summary: 'escalated',
          details: {
            reason: 'completion gate found unfinished work',
            cause: 'completion-gate',
            findings: JSON.stringify([
              { aspect: 'tests', verdict: 'block', reason: 'missing regression' },
            ]),
          },
        },
      ],
    });
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="escalation-reason"]')?.textContent).toContain('completion gate');
    expect(el.querySelector('[data-testid="escalation-gate-source"]')?.textContent).toContain('completion-gate findings');
    const gateItems = el.querySelectorAll('[data-testid="escalation-gate-items"] li');
    expect(gateItems.length).toBe(1);
    expect(gateItems[0].textContent).toContain('missing regression');
    // No code-review grade on file.
    expect(el.querySelector('[data-testid="escalation-review-empty"]')).not.toBeNull();
  });
});
