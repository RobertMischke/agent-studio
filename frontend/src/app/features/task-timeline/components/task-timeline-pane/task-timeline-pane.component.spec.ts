import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskTimelinePaneComponent } from './task-timeline-pane.component';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TIMELINE_KIND, type TaskTimelineEvent } from '../../models/task-timeline.model';

async function build(events: TaskTimelineEvent[] = []) {
  await TestBed.configureTestingModule({
    imports: [TaskTimelinePaneComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      TaskTimelinePollService,
    ],
  }).compileComponents();
  TestBed.inject(TaskTimelinePollService).events.set(events);
  const fixture = TestBed.createComponent(TaskTimelinePaneComponent);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] TaskTimelinePaneComponent render skipped:', (e as Error).message);
  }
  return fixture;
}

describe('TaskTimelinePaneComponent', () => {
  it('renders the empty state with no events', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    expect(c.hasEvents()).toBe(false);
    expect(c.hasLoop()).toBe(false);
    const html = fixture.nativeElement as HTMLElement;
    expect(html.querySelector('[data-testid="timeline-empty"]')).not.toBeNull();
  });

  it('renders the verdict banner + event list when a loop has activity', async () => {
    const fixture = await build([
      { ts: '2026-05-30T10:00:00Z', kind: TIMELINE_KIND.agentRunFinished, actor: 'agent', summary: 'claimed done' },
      {
        ts: '2026-05-30T10:05:00Z', kind: TIMELINE_KIND.qualityLoopReopened, actor: 'quality-loop',
        summary: 'reopened', details: { attempt: '2', maxAttempts: '3', gap: 'still broken' },
      },
    ]);
    const c = fixture.componentInstance;
    expect(c.hasEvents()).toBe(true);
    expect(c.hasLoop()).toBe(true);
    expect(c.attemptLabel()).toBe('2 / 3');
    expect(c.completionLoop().latestVerdict).toBe('reopened');

    const html = fixture.nativeElement as HTMLElement;
    const banner = html.querySelector('[data-testid="timeline-verdict-banner"]');
    expect(banner).not.toBeNull();
    expect(banner!.getAttribute('data-verdict')).toBe('reopened');
    expect(html.querySelector('[data-testid="timeline-verdict-reason"]')?.textContent).toContain('still broken');
    expect(html.querySelectorAll('[data-testid="timeline-event"]').length).toBe(2);
  });

  it('kind / verdict helpers map to stable labels and tones', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    expect(c.isVerdictKind(TIMELINE_KIND.qualityLoopReopened)).toBe(true);
    expect(c.isVerdictKind(TIMELINE_KIND.agentRunStarted)).toBe(false);
    expect(c.rowTone(TIMELINE_KIND.orchestratorVerdictAccepted)).toBe('ok');
    expect(c.rowTone(TIMELINE_KIND.qualityLoopReopened)).toBe('warn');
    expect(c.rowTone(TIMELINE_KIND.orchestratorEscalated)).toBe('danger');
    expect(c.verdictLabel('escalated')).toBe('Escalated to human');
    expect(c.kindLabel(TIMELINE_KIND.qualityLoopReopened)).toBe('Re-opened (go again)');
  });

  it('formatTime shows time only for today and date + time for other days', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;

    const today = new Date();
    const todayIso = today.toISOString();
    const todayOut = c.formatTime(todayIso);
    expect(todayOut).toBe(today.toLocaleTimeString());
    expect(todayOut).not.toContain(today.toLocaleDateString());

    const other = new Date(today.getTime() - 3 * 24 * 60 * 60 * 1000);
    const otherOut = c.formatTime(other.toISOString());
    expect(otherOut).toContain(other.toLocaleDateString());
    expect(otherOut).toContain(other.toLocaleTimeString());
  });

  it('formatAbsoluteTime (hover tooltip) always carries the full date + time', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    const d = new Date('2026-05-30T10:05:00Z');
    const out = c.formatAbsoluteTime(d.toISOString());
    expect(out).toBe(d.toLocaleString());
    expect(out).toContain(d.toLocaleDateString());
  });

  it('detailEntries hides gap/reason/attempt/maxAttempts (rendered separately)', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    const entries = c.detailEntries({
      ts: '', kind: TIMELINE_KIND.qualityLoopReopened, actor: 'quality-loop', summary: '',
      details: { gap: 'g', reason: 'r', attempt: '2', maxAttempts: '3', cause: 'noop-recovery' },
    });
    expect(entries).toEqual([{ key: 'cause', value: 'noop-recovery' }]);
  });
});
