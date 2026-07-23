import { TaskState } from '../models/task.model';
import type { TaskInfo, TaskRunActivityKind } from '../models/task.model';
import type { StructuredTooltip } from 'coding-agent-chat/shared';

export type RunActivityTone = 'active' | 'failed' | 'idle';

export interface RunActivityBadge {
  kind: TaskRunActivityKind;
  label: string;
  tone: RunActivityTone;
  tooltip: StructuredTooltip;
}

/**
 * Grace period for a Progress card that has no live run and no explicit
 * failure. It matches the backend's execution-location freshness window, so a
 * newly claimed card or a run whose registry is briefly rebuilding is not
 * presented as stranded.
 */
export const STALLED_IDLE_THRESHOLD_MS = 3 * 60_000;

export interface StalledTaskState {
  reason: 'failed' | 'idle';
  label: 'Stalled';
  tooltip: string;
}

function latestActivityMs(job: TaskInfo): number | null {
  const instants = [
    job.enteredLaneAt,
    job.phaseEnteredAt,
    job.executionLocation?.lastActivityAt,
    job.executionLocation?.lastHeartbeat,
    job.lastActivity,
    job.createdAt,
  ]
    .map((value) => value ? Date.parse(value) : Number.NaN)
    .filter(Number.isFinite);
  return instants.length > 0 ? Math.max(...instants) : null;
}

function hasLiveRun(job: TaskInfo): boolean {
  if (job.execution?.status === 'running' || job.runner != null || job.runActivity?.kind === 'active') {
    return true;
  }
  const location = job.executionLocation;
  return (location?.state === 'local-running' || location?.state === 'remote-running')
    && location.connectionState === 'connected';
}

/**
 * Pure board-level derivation of an acute stranded Progress task. A failed run
 * needs attention immediately once no process owns it. A task with no recorded
 * failure gets a short grace period before it is called stalled. Scheduled
 * rapid-crash backoff is deliberately excluded because it already has a known
 * recovery path and its own countdown banner.
 */
export function deriveStalledTaskState(
  job: TaskInfo,
  nowMs: number = Date.now(),
  idleThresholdMs: number = STALLED_IDLE_THRESHOLD_MS,
): StalledTaskState | null {
  if (job.state !== TaskState.Progress || hasLiveRun(job)) return null;
  if (job.runActivity?.kind === 'failed-backoff') return null;

  const failed = job.runActivity?.kind === 'failed-idle'
    || job.execution?.status === 'failed'
    || ((job.outcomeIssue?.severity ?? '').toLowerCase() === 'warn')
    || ((job.outcomeIssue?.severity ?? '').toLowerCase() === 'high');
  if (failed) {
    const detail = job.runActivity?.lastError || job.outcomeIssue?.summary || 'The last run ended with an error.';
    return {
      reason: 'failed',
      label: 'Stalled',
      tooltip: `Needs attention: no run is active and the last run failed. ${detail}`,
    };
  }

  const latest = latestActivityMs(job);
  if (latest === null || nowMs - latest <= idleThresholdMs) return null;
  const idleMinutes = Math.max(1, Math.floor((nowMs - latest) / 60_000));
  return {
    reason: 'idle',
    label: 'Stalled',
    tooltip: `Needs attention: this task is still In Progress, but no run is active and no activity has arrived for ${idleMinutes} minutes.`,
  };
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function formatClock(iso: string): string | null {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return null;
  return new Date(ms).toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  });
}

/**
 * ASS-1751: descriptor for the small, quiet run-activity pill shown on a
 * `3-progress` card and in the task-detail header. It disambiguates the three
 * ways a progress card can otherwise look "untouched": a live run occupying a
 * slot, a failed run waiting out the rapid-crash backoff (with the retry time),
 * and an orphan whose run was killed by a backend restart. Returns null off the
 * Progress lane or when the backend attached no `runActivity`. Pure visibility —
 * carries no action. Shared by the board card and the detail header so both
 * read identical copy from one source. The clock is injected (`nowMs`) so
 * callers can drive it from a shared tick signal.
 */
export function buildRunActivityBadge(job: TaskInfo, nowMs: number = Date.now()): RunActivityBadge | null {
  // Lane is authoritative: this is a 3-progress affordance only. The backend
  // overlay only attaches runActivity for Progress, but guard against a stale
  // poll snapshot the same way the execution badge does.
  if (job.state !== TaskState.Progress) return null;
  const activity = job.runActivity;
  if (!activity) return null;

  const attemptLine = activity.attempt > 0
    ? `<div><b>Versuch:</b> ${activity.attempt}</div>`
    : '';
  const errorLine = activity.lastError
    ? `<div><b>Letzter Fehler:</b> ${escapeHtml(activity.lastError)}</div>`
    : '';

  switch (activity.kind) {
    case 'active': {
      const pid = typeof activity.processId === 'number' && activity.processId > 0 ? activity.processId : null;
      return {
        kind: activity.kind,
        label: 'Run aktiv',
        tone: 'active',
        tooltip: {
          title: 'Run aktiv (PID lebt)',
          body: `<div>Ein Run-Prozess läuft und belegt einen Slot.</div>${pid !== null ? `<div><b>PID:</b> ${pid}</div>` : ''}${attemptLine}${errorLine}`,
        },
      };
    }
    case 'failed-backoff': {
      const clock = activity.backoffUntil ? formatClock(activity.backoffUntil) : null;
      const future = activity.backoffUntil ? Date.parse(activity.backoffUntil) > nowMs : false;
      const label = clock && future ? `failed · Backoff bis ${clock}` : 'failed · wartet auf Reissue';
      return {
        kind: activity.kind,
        label,
        tone: 'failed',
        tooltip: {
          title: 'failed — wartet auf Reissue/Review',
          body: `<div>Der letzte Run ist fehlgeschlagen; ein Rapid-Crash-Backoff hält das Re-Pickup${clock && future ? ` bis <b>${clock}</b>` : ''} zurück.</div>${attemptLine}${errorLine}`,
        },
      };
    }
    case 'failed-idle': {
      return {
        kind: activity.kind,
        label: 'failed · kein aktiver Run',
        tone: 'failed',
        tooltip: {
          title: 'failed — kein aktiver Run',
          body: `<div>Der letzte Run ist fehlgeschlagen und aktuell läuft nichts; der Task ist wieder aufnahmebereit.</div>${attemptLine}${errorLine}`,
        },
      };
    }
    case 'no-active-run':
    default: {
      return {
        kind: 'no-active-run',
        label: 'kein aktiver Run',
        tone: 'idle',
        tooltip: {
          title: 'kein aktiver Run',
          body: `<div>Kein Run bearbeitet diesen Task gerade. Er liegt in 3-progress und wartet auf Aufnahme — z. B. wurde ein früherer Run durch einen Backend-Neustart beendet.</div>${attemptLine}${errorLine}`,
        },
      };
    }
  }
}
