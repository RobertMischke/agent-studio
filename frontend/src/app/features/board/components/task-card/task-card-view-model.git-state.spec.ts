import { describe, expect, it } from 'vitest';
import { buildGitStateBadge } from './task-card-view-model';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo } from '../../../../models/task.model';
import type {
  TaskIntegrationStatus,
  TaskProvenanceRecord,
  TaskProvenanceTransition,
} from '../../../../features/git';

/**
 * ASS-1752: the git-state pill must read the provenance ground truth (ASS-1724)
 * and show *where the work actually lives* across the three lifecycle states the
 * bug conflated:
 *   A) active task/<id> worktree (current run, reissue-safe),
 *   B) landed in develop after integrate + teardown (no dead worktree path),
 *   C) sequential run in the shared main checkout (no task branch at all).
 * The old badge guessed purely from the lane, so it lied for B and C and for
 * reissues. These tests pin each state to the provenance facts, not the lane.
 */
function anchor(overrides: Partial<TaskProvenanceTransition> = {}): TaskProvenanceTransition {
  return {
    lane: TaskState.Progress,
    atUtc: '2026-06-10T10:00:00Z',
    branchTip: null,
    workBranchHead: null,
    ...overrides,
  };
}

function provenance(overrides: Partial<TaskProvenanceRecord> = {}): TaskProvenanceRecord {
  return {
    branch: 'task/task-1',
    base: 'base000',
    transitions: [],
    merge: null,
    ...overrides,
  };
}

function integration(overrides: Partial<TaskIntegrationStatus> = {}): TaskIntegrationStatus {
  return {
    status: 'integrated',
    sha: 'ddddddd',
    integrationBranch: 'develop',
    detail: 'anchor-ancestor',
    ...overrides,
  };
}

function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
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
    ...overrides,
  };
}

describe('buildGitStateBadge — lifecycle ground truth (ASS-1752)', () => {
  it('stays quiet on lanes with no useful git context', () => {
    for (const state of [TaskState.Backlog, TaskState.Preparation, TaskState.Ready, TaskState.FailedPickup]) {
      expect(buildGitStateBadge(makeJob({ state }))).toBeNull();
    }
  });

  describe('State A — active task/<id> worktree', () => {
    it('names a prepared/ready task branch before the first commit exists', () => {
      const job = makeJob({
        state: TaskState.Ready,
        commit: null,
        commits: [],
        provenance: provenance({
          branch: 'task/ready-before-first-commit',
          transitions: [anchor({ lane: TaskState.Ready, branchTip: 'bbbbbbb1' })],
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge).not.toBeNull();
      expect(badge!.kind).toBe('pre-merge');
      expect(badge!.label).toBe('task/ready-before-first-commit');
      expect(badge!.tooltip).toContain('bbbbbbb');
    });

    it('names the task branch while a run is live in its worktree', () => {
      const job = makeJob({
        state: TaskState.Progress,
        provenance: provenance({
          branch: 'task/task-1',
          transitions: [anchor({ lane: TaskState.Progress, branchTip: 'aaaaaaa1' })],
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge).not.toBeNull();
      expect(badge!.kind).toBe('pre-merge');
      expect(badge!.label).toBe('task/task-1');
      expect(badge!.glyph).toBe('⎇');
      expect(badge!.tooltip).toContain('aaaaaaa'); // current tip surfaced
    });

    it('tracks the CURRENT attempt on a reissue (newest branchTip wins)', () => {
      // First attempt cut tip aaa…; a reissue re-ran and the latest transition
      // anchored the new tip ccc…. The pill must show the live worktree, not the
      // stale earlier run.
      const job = makeJob({
        state: TaskState.Progress,
        provenance: provenance({
          transitions: [
            anchor({ lane: TaskState.Progress, branchTip: 'aaaaaaa1' }),
            anchor({ lane: TaskState.HumanReview, branchTip: 'bbbbbbb2' }),
            anchor({ lane: TaskState.Progress, branchTip: 'ccccccc3' }),
          ],
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.tooltip).toContain('ccccccc');
      expect(badge!.tooltip).not.toContain('aaaaaaa');
    });

    it('keeps showing the worktree for an escalated-conflict run (branch alive, not merged)', () => {
      // Escalated is NOT a post-integration review lane: a conflict escalation
      // leaves the worktree alive, so the branch must still show.
      const job = makeJob({
        state: TaskState.Escalated,
        provenance: provenance({
          transitions: [anchor({ lane: TaskState.Escalated, branchTip: 'eeeeeee1' })],
          merge: null,
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.kind).toBe('pre-merge');
      expect(badge!.label).toBe('task/task-1');
    });
  });

  describe('State B — landed in develop (no dead worktree path)', () => {
    it('shows develop @sha from the recorded merge fact, even in a review lane', () => {
      // Parallel run already auto-integrated + torn down; it sits in auto-review.
      // The card must NOT show the dead task/<id> worktree.
      const job = makeJob({
        state: TaskState.AutoReview,
        provenance: provenance({
          transitions: [anchor({ lane: TaskState.AutoReview, branchTip: 'aaaaaaa1' })],
          merge: {
            mergeCommit: 'ddddddd9',
            workBranchHeadBefore: 'dev0000',
            workBranchHeadAfter: 'ddddddd9',
            atUtc: '2026-06-10T10:30:00Z',
          },
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.kind).toBe('post-merge');
      expect(badge!.label).toBe('develop @ddddddd');
      expect(badge!.label).not.toContain('task/');
    });

    it('does not treat Human Review plus branch provenance as integration proof', () => {
      const job = makeJob({
        state: TaskState.HumanReview,
        provenance: provenance({
          transitions: [anchor({ lane: TaskState.HumanReview, branchTip: 'aaaaaaa1' })],
          merge: null,
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.kind).toBe('pre-merge');
      expect(badge!.label).toBe('task/task-1');
    });

    it('shows target membership from the canonical integration status', () => {
      const job = makeJob({
        state: TaskState.Completed,
        provenance: provenance(),
        integration: integration({ sha: '0ddba11' }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.kind).toBe('post-merge');
      expect(badge!.label).toBe('develop @0ddba11');
    });

    it('ignores a remembered merge attempt when target membership is pending', () => {
      const job = makeJob({
        state: TaskState.Completed,
        provenance: provenance({
          merge: {
            mergeCommit: 'remembered',
            workBranchHeadBefore: null,
            workBranchHeadAfter: null,
            atUtc: '2026-06-10T10:30:00Z',
          },
        }),
        integration: integration({ status: 'pending', sha: null, detail: 'not present' }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.kind).toBe('pre-merge');
      expect(badge!.label).toBe('main checkout');
    });
  });

  describe('State C — sequential run, shared main checkout', () => {
    it('says "main checkout" when no task branch was ever cut', () => {
      // Sequential run: provenance exists but no transition ever saw a branchTip.
      const job = makeJob({
        state: TaskState.Progress,
        provenance: provenance({
          transitions: [anchor({ lane: TaskState.Progress, branchTip: null })],
          merge: null,
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.kind).toBe('pre-merge');
      expect(badge!.label).toBe('main checkout');
      expect(badge!.glyph).toBe('✎');
      expect(badge!.label).not.toContain('task/');
    });

    it('says "main checkout" when there is no provenance at all (legacy card)', () => {
      const job = makeJob({ state: TaskState.Progress, provenance: null });

      const badge = buildGitStateBadge(job);

      expect(badge!.label).toBe('main checkout');
    });

    it('a sequential card in auto-review is NOT falsely marked landed', () => {
      // No branchTip anywhere -> the post-integration-review "landed" path must
      // not fire; sequential review work stays a main-checkout read.
      const job = makeJob({
        state: TaskState.AutoReview,
        provenance: provenance({
          transitions: [anchor({ lane: TaskState.AutoReview, branchTip: null })],
          merge: null,
        }),
      });

      const badge = buildGitStateBadge(job);

      expect(badge!.label).toBe('main checkout');
    });
  });

  describe('Archive', () => {
    it('collapses to a quiet tagged pill', () => {
      const badge = buildGitStateBadge(makeJob({ state: TaskState.Archive }));

      expect(badge!.kind).toBe('tagged');
      expect(badge!.label).toBe('tagged');
    });
  });
});
