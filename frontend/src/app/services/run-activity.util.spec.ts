import { describe, expect, it } from 'vitest';
import { buildRunActivityBadge, deriveStalledTaskState, STALLED_IDLE_THRESHOLD_MS } from './run-activity.util';
import { TaskState } from '../models/task.model';
import type { TaskInfo, TaskRunActivity } from '../models/task.model';

/**
 * ASS-1751: the run-activity pill makes a 3-progress card self-explanatory. The
 * three states that otherwise all look "untouched" must each render a distinct,
 * quiet pill:
 *   (a) failed + rapid-crash backoff  → "failed · Backoff bis HH:MM" with the time,
 *   (b) orphan / no active run        → "kein aktiver Run",
 *   (c) active run                    → "Run aktiv" (+ PID in the tooltip).
 * Plus a failed-idle variant for a failed run with no backoff. These tests pin
 * the label, tone, kind and tooltip for each, and the lane/absence guards.
 */
const NOW = Date.parse('2026-06-10T12:00:00Z');

function makeJob(runActivity: TaskRunActivity | null, overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ATP-1',
    title: 'Task 1',
    state: TaskState.Progress,
    order: 1,
    agent: 'codex',
    createdAt: '2026-06-10T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-06-10T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    runActivity,
    ...overrides,
  } as TaskInfo;
}

describe('buildRunActivityBadge — 3-progress run states (ASS-1751)', () => {
  it('returns null off the Progress lane even when runActivity is present', () => {
    const job = makeJob({ kind: 'active', processId: 10, attempt: 0 }, { state: TaskState.Ready });
    expect(buildRunActivityBadge(job, NOW)).toBeNull();
  });

  it('returns null when the backend attached no runActivity', () => {
    expect(buildRunActivityBadge(makeJob(null), NOW)).toBeNull();
    expect(buildRunActivityBadge(makeJob(undefined as unknown as null), NOW)).toBeNull();
  });

  describe('(c) active run', () => {
    it('shows "Run aktiv" with the PID in the tooltip', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'active', processId: 4242, attempt: 0 }), NOW);
      expect(badge).not.toBeNull();
      expect(badge!.kind).toBe('active');
      expect(badge!.tone).toBe('active');
      expect(badge!.label).toBe('Run aktiv');
      expect(badge!.tooltip.body).toContain('4242');
    });

    it('omits the PID line when no live pid is known', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'active', processId: 0, attempt: 0 }), NOW);
      expect(badge!.tooltip.body).not.toContain('PID');
    });
  });

  describe('(a) failed + backoff', () => {
    it('shows the retry clock when the backoff is in the future', () => {
      const backoffUntil = new Date(NOW + 90_000).toISOString(); // +90s
      const badge = buildRunActivityBadge(
        makeJob({ kind: 'failed-backoff', backoffUntil, attempt: 2, lastError: 'git push rejected' }),
        NOW,
      );
      expect(badge!.kind).toBe('failed-backoff');
      expect(badge!.tone).toBe('failed');
      expect(badge!.label).toMatch(/^failed · Backoff bis \d{2}:\d{2}$/);
      expect(badge!.tooltip.body).toContain('Versuch:');
      expect(badge!.tooltip.body).toContain('git push rejected');
    });

    it('falls back to "wartet auf Reissue" when the backoff has already elapsed', () => {
      const backoffUntil = new Date(NOW - 5_000).toISOString();
      const badge = buildRunActivityBadge(
        makeJob({ kind: 'failed-backoff', backoffUntil, attempt: 3 }),
        NOW,
      );
      expect(badge!.label).toBe('failed · wartet auf Reissue');
    });

    it('escapes HTML in the last-error tooltip line', () => {
      const backoffUntil = new Date(NOW + 60_000).toISOString();
      const badge = buildRunActivityBadge(
        makeJob({ kind: 'failed-backoff', backoffUntil, attempt: 1, lastError: '<img src=x onerror=alert(1)>' }),
        NOW,
      );
      expect(badge!.tooltip.body).toContain('&lt;img');
      expect(badge!.tooltip.body).not.toContain('<img');
    });
  });

  describe('failed + idle (no backoff)', () => {
    it('shows "failed · kein aktiver Run"', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'failed-idle', attempt: 1, lastError: 'missing sentinel' }), NOW);
      expect(badge!.kind).toBe('failed-idle');
      expect(badge!.tone).toBe('failed');
      expect(badge!.label).toBe('failed · kein aktiver Run');
      expect(badge!.tooltip.body).toContain('missing sentinel');
    });
  });

  describe('(b) orphan / no active run', () => {
    it('shows the muted "kein aktiver Run" pill', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'no-active-run', attempt: 0 }), NOW);
      expect(badge!.kind).toBe('no-active-run');
      expect(badge!.tone).toBe('idle');
      expect(badge!.label).toBe('kein aktiver Run');
      expect(badge!.tooltip.body).toContain('Backend-Neustart');
    });

    it('omits the attempt line when there is no recorded failure streak', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'no-active-run', attempt: 0 }), NOW);
      expect(badge!.tooltip.body).not.toContain('Versuch:');
    });
  });
});

describe('deriveStalledTaskState', () => {
  it('keeps a freshly started Progress card healthy during the idle grace period', () => {
    const job = makeJob(
      { kind: 'no-active-run', attempt: 0 },
      { enteredLaneAt: new Date(NOW - STALLED_IDLE_THRESHOLD_MS).toISOString() },
    );

    expect(deriveStalledTaskState(job, NOW)).toBeNull();
  });

  it('marks a no-active-run card stalled only after the idle threshold', () => {
    const job = makeJob(
      { kind: 'no-active-run', attempt: 0 },
      { enteredLaneAt: new Date(NOW - STALLED_IDLE_THRESHOLD_MS - 1).toISOString(), lastActivity: '' },
    );

    expect(deriveStalledTaskState(job, NOW)).toMatchObject({ reason: 'idle', label: 'Stalled' });
  });

  it('marks an explicitly failed idle run stalled immediately', () => {
    const job = makeJob(
      { kind: 'failed-idle', attempt: 1, lastError: 'agent did not produce a reply' },
      { enteredLaneAt: new Date(NOW - 1_000).toISOString() },
    );

    expect(deriveStalledTaskState(job, NOW)).toMatchObject({ reason: 'failed', label: 'Stalled' });
  });

  it('uses an acute outcome issue when the runner registry only reports no-active-run', () => {
    const job = makeJob(
      { kind: 'no-active-run', attempt: 0 },
      {
        enteredLaneAt: new Date(NOW - 1_000).toISOString(),
        outcomeIssue: {
          kind: 'classifier-unknown',
          label: 'Unclear',
          severity: 'Warn',
          summary: 'Tool router reported an execution error',
          lastSeenAt: new Date(NOW - 2_000).toISOString(),
        },
      },
    );

    expect(deriveStalledTaskState(job, NOW)).toMatchObject({ reason: 'failed' });
  });

  it('does not flag live or scheduled-retry cards', () => {
    expect(deriveStalledTaskState(
      makeJob({ kind: 'active', processId: 42, attempt: 0 }),
      NOW,
    )).toBeNull();
    expect(deriveStalledTaskState(
      makeJob({ kind: 'failed-backoff', attempt: 1, backoffUntil: new Date(NOW + 60_000).toISOString() }),
      NOW,
    )).toBeNull();
  });
});
