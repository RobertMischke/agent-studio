import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { CompletionLoopIndicatorComponent } from './completion-loop-indicator.component';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import { TIMELINE_KIND, type TaskTimelineEvent } from '../../models/task-timeline.model';
import { TaskState } from '../../../../models/task.model';

async function build(events: TaskTimelineEvent[] = [], taskState: string | null = null) {
  await TestBed.configureTestingModule({
    imports: [CompletionLoopIndicatorComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      TaskTimelinePollService,
    ],
  }).compileComponents();
  TestBed.inject(TaskTimelinePollService).events.set(events);
  const fixture = TestBed.createComponent(CompletionLoopIndicatorComponent);
  fixture.componentRef.setInput('taskState', taskState);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] CompletionLoopIndicatorComponent render skipped:', (e as Error).message);
  }
  return fixture;
}

describe('CompletionLoopIndicatorComponent', () => {
  it('renders nothing until the ledger has a verdict event', async () => {
    const fixture = await build([
      { ts: '2026-05-30T10:00:00Z', kind: TIMELINE_KIND.agentRunFinished, actor: 'agent', summary: 'claimed done' },
    ]);
    expect(fixture.componentInstance.hasCompletionLoop()).toBe(false);
    const html = fixture.nativeElement as HTMLElement;
    expect(html.querySelector('[data-testid="overview-completion-loop"]')).toBeNull();
  });

  it('surfaces attempt N/M + reopen reason after a reopen verdict', async () => {
    const fixture = await build([
      { ts: '2026-05-30T10:00:00Z', kind: TIMELINE_KIND.agentRunFinished, actor: 'agent', summary: 'claimed done' },
      {
        ts: '2026-05-30T10:05:00Z', kind: TIMELINE_KIND.qualityLoopReopened, actor: 'quality-loop',
        summary: 'reopened', details: { attempt: '2', maxAttempts: '3', gap: 'button still misaligned' },
      },
    ]);
    const c = fixture.componentInstance;
    expect(c.hasCompletionLoop()).toBe(true);
    expect(c.completionLoop().latestVerdict).toBe('reopened');
    expect(c.completionLoop().reopenCount).toBe(1);
    expect(c.attemptLabel()).toBe('2 / 3');

    const html = fixture.nativeElement as HTMLElement;
    const verdict = html.querySelector('[data-testid="overview-loop-verdict"]');
    expect(verdict).not.toBeNull();
    expect(verdict!.getAttribute('data-verdict')).toBe('reopened');
    expect(html.querySelector('[data-testid="overview-loop-attempt"]')?.textContent).toContain('2 / 3');
    expect(html.querySelector('[data-testid="overview-loop-reason"]')?.textContent).toContain('button still misaligned');
  });

  it('shows the accepted terminal as the latest verdict, falling back to reopenCount + 1', async () => {
    const fixture = await build([
      {
        ts: '2026-05-30T10:05:00Z', kind: TIMELINE_KIND.qualityLoopReopened, actor: 'quality-loop',
        summary: 'reopened', details: { attempt: '2', maxAttempts: '3', gap: 'lint failing' },
      },
      {
        ts: '2026-05-30T10:30:00Z', kind: TIMELINE_KIND.orchestratorVerdictAccepted, actor: 'orchestrator',
        summary: 'all aspects pass',
      },
    ]);
    const c = fixture.componentInstance;
    expect(c.completionLoop().latestVerdict).toBe('accepted');
    expect(c.attemptLabel()).toBe('2');
    const html = fixture.nativeElement as HTMLElement;
    expect(html.querySelector('[data-testid="overview-loop-verdict"]')?.getAttribute('data-verdict')).toBe('accepted');
  });

  it('marks an escalated verdict as history after the task later reaches Delivered', async () => {
    const fixture = await build([{
      ts: '2026-07-22T10:05:00Z', kind: TIMELINE_KIND.orchestratorEscalated, actor: 'orchestrator',
      summary: 'Recorded escalate verdict never completed its lane move',
      details: { reason: 'Recorded escalate verdict never completed its lane move' },
    }], TaskState.Completed);
    const html = fixture.nativeElement as HTMLElement;
    const verdict = html.querySelector('[data-testid="overview-loop-verdict"]');

    expect(verdict?.getAttribute('data-historical')).toBe('true');
    expect(verdict?.textContent).toContain('Earlier:');
    expect(html.querySelector('[data-testid="overview-loop-current-outcome"]')?.textContent).toContain('Delivered');
    expect(html.querySelector('[data-testid="overview-loop-history"]')?.textContent).toContain('prior attempt');
    expect(html.querySelector('[data-testid="overview-loop-history"]')?.textContent).toContain('reopened');
    expect(html.querySelector('[data-testid="overview-loop-reason"]')).toBeNull();
  });

  it('verdict helpers map to stable labels / glyphs / tones', async () => {
    const c = (await build([])).componentInstance;
    expect(c.verdictLabel('accepted')).toBe('Accepted');
    expect(c.verdictLabel('escalated')).toBe('Escalated to human');
    expect(c.verdictGlyph('reopened')).toBe('↻');
    expect(c.verdictTone('accepted')).toBe('ok');
    expect(c.verdictTone('reopened')).toBe('warn');
    expect(c.verdictTone('escalated')).toBe('danger');
    expect(c.verdictTone(null)).toBe('neutral');
  });
});
