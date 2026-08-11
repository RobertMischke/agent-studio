import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import {
  deriveCompletionLoop,
  timelineEventIdentity,
  type CompletionLoopState,
  type TaskTimelineEvent,
} from '../../../features/task-timeline';
import { TaskService } from '../../../services/task.service';
import { TaskBackgroundPoller } from './task-background-poller';
import { stripAnsi } from '../../../utils/ansi-text';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import type { TaskInfo } from '../../../models/task.model';

/**
 * Polls the per-task event ledger (`/api/tasks/{id}/timeline`, ADR-0049 /
 * ASS-566) while a job is open. The ledger is the union of lifecycle
 * events - prompt creation, agent runs, pipeline steps, and the
 * orchestrator's completion-loop verdicts (accept / reopen / escalate).
 *
 * 10 s cadence: the timeline changes at the granularity of a run
 * finishing or the orchestrator emitting a verdict, not per-frame, so a
 * slower poll than the 5 s live-CLI services is plenty. Matches the
 * orchestrator-log cadence.
 */
@Injectable()
export class TaskTimelinePollService extends TaskBackgroundPoller<TaskTimelineEvent[]> {
  private readonly jobService = inject(TaskService);
  private readonly jobsHub = inject(JobsHubClient);
  private activeTask: Pick<TaskInfo, 'id' | 'watchPath'> | null = null;

  protected readonly intervalMs = 10_000;

  /** Raw ledger rows in chronological (append) order. */
  readonly events = signal<TaskTimelineEvent[]>([]);

  private readonly liveAppendEffect = effect(() => {
    const pushed = this.jobsHub.timelineEventAppended();
    const active = this.activeTask;
    if (!pushed || !active || pushed.jobId !== active.id
      || normalizeWatchPath(pushed.watchPath) !== normalizeWatchPath(active.watchPath)) return;
    const [event] = sanitizeTimelineEvents([pushed.timelineEvent]);
    this.events.update(current => appendTimelineEvent(current, event));
  });

  /**
   * Derived "where is the completion loop right now" summary the
   * Overview attempt-cycle indicator binds to. Recomputed whenever the
   * ledger changes.
   */
  readonly completionLoop = computed<CompletionLoopState>(() =>
    deriveCompletionLoop(this.events()),
  );

  override syncTo(info: TaskInfo | null | undefined): void {
    this.activeTask = info ? { id: info.id, watchPath: info.watchPath } : null;
    super.syncTo(info);
  }

  protected fetch(jobId: string, watchPath: string): Observable<TaskTimelineEvent[]> {
    return this.jobService.getTaskTimeline(jobId, watchPath);
  }

  protected applyResponse(res: TaskTimelineEvent[]): void {
    const sanitized = sanitizeTimelineEvents(res);
    this.events.update(current => reconcileTimelineEvents(current, sanitized));
  }

  protected clearValue(): void {
    this.events.set([]);
  }

  override ngOnDestroy(): void {
    this.liveAppendEffect.destroy();
    super.ngOnDestroy();
  }
}

/** Preserve existing object identities when an append-only poll adds rows. */
export function reconcileTimelineEvents(
  current: readonly TaskTimelineEvent[],
  incoming: readonly TaskTimelineEvent[],
): TaskTimelineEvent[] {
  if (current.length > incoming.length) return [...incoming];
  for (let index = 0; index < current.length; index++) {
    if (timelineEventIdentity(current[index]) !== timelineEventIdentity(incoming[index])) {
      return [...incoming];
    }
  }
  if (current.length === incoming.length) return current as TaskTimelineEvent[];
  return [...current, ...incoming.slice(current.length)];
}

/** Append one pushed ledger row once, even when a convergence poll races it. */
export function appendTimelineEvent(
  current: readonly TaskTimelineEvent[],
  incoming: TaskTimelineEvent,
): TaskTimelineEvent[] {
  const identity = timelineEventIdentity(incoming);
  return current.some(event => timelineEventIdentity(event) === identity)
    ? current as TaskTimelineEvent[]
    : [...current, incoming];
}

function normalizeWatchPath(value: string): string {
  return value.replaceAll('\\', '/').replace(/\/+$/, '').toLowerCase();
}

/** Plain-text timeline surfaces must never expose terminal control codes. */
export function sanitizeTimelineEvents(
  events: readonly TaskTimelineEvent[] | null | undefined,
): TaskTimelineEvent[] {
  return (events ?? []).map((event) => ({
    ...event,
    summary: stripAnsi(event.summary),
    details: event.details
      ? Object.fromEntries(Object.entries(event.details).map(([key, value]) => [key, stripAnsi(value)]))
      : event.details,
  }));
}
