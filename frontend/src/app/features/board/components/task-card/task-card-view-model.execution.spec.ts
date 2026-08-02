import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { buildExecutionBadge } from './task-card-view-model';

function job(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    title: 'Task 1',
    state: '3-progress',
    order: 1,
    agent: 'codex',
    createdAt: '2026-07-26T20:00:00Z',
    watchPath: '/workspace',
    projectName: 'Test',
    folderPath: '/workspace/3-progress/task-1',
    lastActivity: '2026-07-26T20:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: {
      jobId: 'task-1',
      taskKey: 'test::task-1',
      processId: 0,
      startedAt: '2026-07-26T20:00:00Z',
      status: 'failed',
      exitCode: 1,
      durationSeconds: 1,
      model: 'gpt-5',
      runOutcome: 'failed',
    },
    commit: null,
    ...overrides,
  } as TaskInfo;
}

describe('buildExecutionBadge run liveness', () => {
  it('shows a running badge when an active pre-step supersedes stale failed execution', () => {
    expect(buildExecutionBadge(job({
      runActivity: { kind: 'failed-idle', attempt: 1, lastError: 'stale failure' },
      liveStatus: {
        attempt: 2,
        activeStep: {
          stepId: 'pre-worktree-create',
          displayName: 'Create worktree',
          kind: 'pre',
        },
        nextSteps: [{ stepId: 'core', displayName: 'Agent execution' }],
      },
    }))).toEqual({ label: 'Running live', tone: 'running' });
  });

  it('keeps a genuinely terminal execution failed when no positive live signal exists', () => {
    expect(buildExecutionBadge(job({
      runActivity: { kind: 'failed-idle', attempt: 1, lastError: 'real failure' },
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'core', displayName: 'Agent execution' }],
      },
    }))).toEqual({ label: 'Failed (1)', tone: 'failed' });
  });
});
