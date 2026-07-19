import { describe, expect, it } from 'vitest';
import { buildMergeSignal } from './task-card-view-model';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo } from '../../../../models/task.model';
import type { TaskCommitInfo, TaskMergeSignal, TaskProvenanceRecord } from '../../../../features/git';

/**
 * AGT-2046 / AGT-2063: the always-on two-segment merge signal ([develop|main]).
 * Primary source is the backend-computed `mergeSignal`; the four state
 * combinations the card must render are exercised here (each on a card that
 * actually has a task commit), plus the graceful degradation when the batched
 * signal is absent, plus the AGT-2063 gate: a card WITHOUT a task commit renders
 * no signal at all, even when a backend `mergeSignal` / branch tip / merge fact
 * is present (those anchor off the branch base, which is trivially in
 * develop/main and used to paint commit-less cards as "merged").
 */
function commit(overrides: Partial<TaskCommitInfo> = {}): TaskCommitInfo {
  return {
    sha: 'c0ffee1234',
    shortSha: 'c0ffee1',
    message: 'feat: task work',
    filesChanged: 1,
    files: ['src/x.ts'],
    at: '2026-07-10T09:20:00Z',
    ...overrides,
  };
}

function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ATP-1',
    title: 'Task 1',
    state: TaskState.HumanReview,
    order: 1,
    agent: 'codex',
    createdAt: '2026-07-10T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/5-human-review/task-1',
    lastActivity: '2026-07-10T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    // Default fixture carries a task commit so the signal is allowed to render;
    // the AGT-2063 gate (no commit -> no signal) is exercised explicitly below.
    commits: [commit()],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}

function signal(overrides: Partial<TaskMergeSignal> = {}): TaskMergeSignal {
  return {
    branch: 'task/ATP-1',
    inIntegration: false,
    inRelease: false,
    integrationBranch: 'develop',
    releaseBranch: 'main',
    integrationSha: null,
    releaseSha: null,
    ...overrides,
  };
}

function provenance(overrides: Partial<TaskProvenanceRecord> = {}): TaskProvenanceRecord {
  return { branch: 'task/ATP-1', base: 'base000', transitions: [], merge: null, ...overrides };
}

describe('buildMergeSignal — four merge-state combinations (AGT-2046)', () => {
  it('is null on a pre-work card with no task commit', () => {
    expect(buildMergeSignal(makeJob({ state: TaskState.Backlog, commits: [] }))).toBeNull();
  });

  describe('AGT-2063 - a card without a task commit renders NO signal', () => {
    it('suppresses the signal even when a backend mergeSignal is present', () => {
      // The exact operator bug: an empty card carried a backend mergeSignal (its
      // branch base is trivially in develop/main), so it showed a merge state.
      expect(buildMergeSignal(makeJob({
        commit: null,
        commits: [],
        mergeSignal: signal({ inIntegration: true, integrationSha: 'a1b2c3d' }),
      }))).toBeNull();
    });

    it('suppresses the signal even with a branch-tip / merge-fact anchor but no commit', () => {
      expect(buildMergeSignal(makeJob({
        commit: null,
        commits: [],
        mergeSignal: null,
        provenance: provenance({
          merge: { mergeCommit: 'deadbeef1234', workBranchHeadBefore: null, workBranchHeadAfter: null, atUtc: '2026-07-10T10:00:00Z' },
          transitions: [{ lane: TaskState.HumanReview, atUtc: '2026-07-10T10:00:00Z', branchTip: 'tip123', workBranchHead: 'dev999' }],
        }),
      }))).toBeNull();
    });
  });

  it('[d empty | m empty] — on branch only, neither develop nor main', () => {
    const view = buildMergeSignal(makeJob({ mergeSignal: signal() }))!;
    expect(view.develop.merged).toBe(false);
    expect(view.main.merged).toBe(false);
    expect(view.develop.short).toBe('d');
    expect(view.main.short).toBe('m');
    expect(view.tooltip).toContain('Not yet in develop');
    expect(view.tooltip).toContain('Not in main');
    expect(view.ariaLabel).toBe('Merge status: not in develop, not in main');
  });

  it('[d filled | m empty] — merged into develop but not main, with the since-sha', () => {
    const view = buildMergeSignal(makeJob({
      mergeSignal: signal({ inIntegration: true, integrationSha: 'a1b2c3d' }),
    }))!;
    expect(view.develop.merged).toBe(true);
    expect(view.main.merged).toBe(false);
    expect(view.develop.sha).toBe('a1b2c3d');
    expect(view.tooltip).toContain('In develop since a1b2c3d');
    expect(view.tooltip).toContain('Not in main');
  });

  it('[d filled | m filled] — released to main', () => {
    const view = buildMergeSignal(makeJob({
      mergeSignal: signal({ inIntegration: true, inRelease: true, integrationSha: 'a1b2c3d', releaseSha: 'ffee001' }),
    }))!;
    expect(view.develop.merged).toBe(true);
    expect(view.main.merged).toBe(true);
    expect(view.tooltip).toContain('In main');
    expect(view.ariaLabel).toBe('Merge status: in develop, in main');
  });

  it('honours a non-default integration/release branch name in labels and tooltip', () => {
    const view = buildMergeSignal(makeJob({
      mergeSignal: signal({ inIntegration: true, integrationBranch: 'integration', releaseBranch: 'release' }),
    }))!;
    expect(view.develop.label).toBe('integration');
    expect(view.main.label).toBe('release');
    expect(view.tooltip).toContain('In integration');
    expect(view.tooltip).toContain('Not in release');
  });

  it('carries the branch name into the tooltip', () => {
    const view = buildMergeSignal(makeJob({ mergeSignal: signal({ branch: 'task/xyz' }) }))!;
    expect(view.branch).toBe('task/xyz');
    expect(view.tooltip).toContain('Branch: task/xyz');
  });

  describe('graceful degradation without the batched signal', () => {
    it('derives develop from the persisted merge fact; main stays unknown/false', () => {
      const view = buildMergeSignal(makeJob({
        mergeSignal: null,
        provenance: provenance({ merge: { mergeCommit: 'deadbeef1234', workBranchHeadBefore: null, workBranchHeadAfter: null, atUtc: '2026-07-10T10:00:00Z' } }),
      }))!;
      expect(view.develop.merged).toBe(true);
      expect(view.develop.sha).toBe('deadbee');
      expect(view.main.merged).toBe(false);
    });

    it('treats the terminal Completed lane as merged-to-develop (given an anchor)', () => {
      // An anchor must exist for any signal to render (matches the backend, which
      // only computes a signal for anchored cards); the Completed lane then proves
      // develop without the batched graph result.
      const view = buildMergeSignal(makeJob({
        state: TaskState.Completed,
        mergeSignal: null,
        provenance: provenance({ transitions: [{ lane: TaskState.Completed, atUtc: '2026-07-10T10:00:00Z', branchTip: 'tip123', workBranchHead: 'dev999' }] }),
      }))!;
      expect(view.develop.merged).toBe(true);
      expect(view.main.merged).toBe(false);
    });

    it('shows the signal for a card with only the legacy singular commit anchor', () => {
      const view = buildMergeSignal(makeJob({
        mergeSignal: null,
        provenance: null,
        commits: [],
        commit: { sha: 'abc123', shortSha: 'abc123', message: 'x', filesChanged: 1, files: [], at: '2026-07-10T10:00:00Z' },
      }))!;
      expect(view).not.toBeNull();
      expect(view.develop.merged).toBe(false);
    });
  });
});
