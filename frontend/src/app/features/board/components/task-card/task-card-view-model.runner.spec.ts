import { describe, expect, it } from 'vitest';
import { buildRunnerBadge } from './task-card-view-model';
import { TaskState } from '../../../../models/task.model';
import type { CliExecution, TaskInfo, TaskRunnerInfo } from '../../../../models/task.model';

/**
 * AGT-2003: the runner badge lets the operator tell a locally-executed card
 * apart from one running on a remote runner ("da fehlt der Abgleich im Stable
 * Board"). A remote runner holds the run lease (ADR-0060); a local in-process
 * run holds none. These tests pin the lokal-vs-remote decision to that signal.
 */
function runningExec(overrides: Partial<CliExecution> = {}): CliExecution {
  return {
    jobId: 'task-1',
    taskKey: 'test::task-1',
    processId: 4242,
    startedAt: '2026-07-09T10:00:00Z',
    status: 'running',
    exitCode: null,
    durationSeconds: null,
    model: 'claude-sonnet-4.5',
    ...overrides,
  };
}

function remoteRunner(overrides: Partial<TaskRunnerInfo> = {}): TaskRunnerInfo {
  return {
    runnerId: 'agent-runner-01@linux-host',
    runnerName: 'agent-runner-01',
    hostname: 'linux-host',
    backendName: 'remote',
    isRemote: true,
    leaseId: 'lease-abc',
    fencingToken: 7,
    acquiredAt: '2026-07-09T10:00:00Z',
    ...overrides,
  };
}

function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'PT-578',
    title: 'Task 1',
    state: TaskState.Progress,
    order: 1,
    agent: 'claude',
    createdAt: '2026-07-09T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-07-09T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: runningExec(),
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}

describe('buildRunnerBadge (AGT-2003)', () => {
  it('shows the remote runner name with an arrow glyph when a remote lease is held', () => {
    const badge = buildRunnerBadge(makeJob({ runner: remoteRunner() }));
    expect(badge).not.toBeNull();
    expect(badge!.kind).toBe('remote');
    expect(badge!.label).toBe('agent-runner-01');
    expect(badge!.glyph).toBe('⇥');
    expect(badge!.tooltip).toContain('agent-runner-01');
    expect(badge!.tooltip).toContain('linux-host');
  });

  it('falls back to the runner id when the remote lease carries no name', () => {
    const badge = buildRunnerBadge(
      makeJob({ runner: remoteRunner({ runnerName: '' }) }),
    );
    expect(badge!.kind).toBe('remote');
    expect(badge!.label).toBe('agent-runner-01@linux-host');
  });

  it('shows a quiet "lokal" chip for an in-process run with no remote lease', () => {
    const badge = buildRunnerBadge(makeJob({ runner: null }));
    expect(badge).not.toBeNull();
    expect(badge!.kind).toBe('local');
    expect(badge!.label).toBe('lokal');
    expect(badge!.glyph).toBe('');
  });

  it('treats a same-backend (non-remote) lease as a local run', () => {
    const badge = buildRunnerBadge(
      makeJob({ runner: remoteRunner({ isRemote: false, runnerName: 'dev@host' }) }),
    );
    expect(badge!.kind).toBe('local');
    expect(badge!.label).toBe('lokal');
  });

  it('stays null on a progress card that is not actually running and has no lease', () => {
    expect(buildRunnerBadge(makeJob({ execution: null, runner: null }))).toBeNull();
    expect(
      buildRunnerBadge(makeJob({ execution: runningExec({ status: 'completed' }), runner: null })),
    ).toBeNull();
  });

  it('stays null outside the progress lane even if a stale lease lingers', () => {
    // The backend only folds Runner on for Progress-lane cards, but the badge
    // must not light up on a review/completed card if a payload ever carries one
    // without a live run.
    expect(
      buildRunnerBadge(makeJob({ state: TaskState.AutoReview, execution: null, runner: null })),
    ).toBeNull();
  });
});
