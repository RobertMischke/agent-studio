import { ChangeDetectionStrategy, Component } from '@angular/core';

import { TaskState } from '../../../app/models/task.model';
import type { TaskInfo } from '../../../app/models/task.model';
import { buildGitStateBadge, type GitStateBadge, type GitStateBadgeKind } from '../../../app/features/board';

/**
 * ASS-1752 visual harness for the git-state pill on the task card.
 *
 * It renders the production pill markup + SCSS (copied verbatim from
 * `task-card.component`) for each lifecycle state the bug conflated, side by
 * side:
 *  - BEFORE: the old lane-only `buildGitStateBadge` (reconstructed verbatim from
 *    git rev 4a89f721^, the commit prior to the fix) — it lied for landed and
 *    sequential runs and for reissues.
 *  - AFTER: the shipped `buildGitStateBadge` imported from the real view-model,
 *    reading the provenance ground truth (ASS-1724).
 *
 * Backend-free: the pill is a pure function of `TaskInfo`, so no services, HTTP,
 * or SignalR are needed — exactly the precedent set by the other src/mockups/*.
 */

// ---------------------------------------------------------------------------
// BEFORE: the old lane-only badge, verbatim from `git show 4a89f721^`.
// ---------------------------------------------------------------------------
const GIT_STATE_BY_LANE: Readonly<Record<string, GitStateBadgeKind>> = {
  [TaskState.Progress]: 'pre-merge',
  [TaskState.CodeNotComplete]: 'pre-merge',
  [TaskState.AutoReview]: 'pre-merge',
  [TaskState.HumanReview]: 'pre-merge',
  [TaskState.Escalated]: 'pre-merge',
  [TaskState.Completed]: 'post-merge',
  [TaskState.Archive]: 'tagged',
};

function buildGitStateBadgeOld(job: TaskInfo): GitStateBadge | null {
  const kind = GIT_STATE_BY_LANE[job.state];
  if (!kind) return null;
  switch (kind) {
    case 'pre-merge': {
      const branch = `task/${job.key || job.id}`;
      return {
        kind,
        label: branch,
        glyph: '⎇',
        tooltip: `Git state: pre-merge — this task's work lives on its task branch (${branch}) and is not yet integrated into develop.`,
      };
    }
    case 'post-merge':
      return {
        kind,
        label: 'develop',
        glyph: '⬇',
        tooltip: "Git state: post-merge — this task's commits are integrated into the develop branch.",
      };
    case 'tagged':
      return {
        kind,
        label: 'tagged',
        glyph: '🏷',
        tooltip: "Git state: archived — this task is out of the active git flow; its work, if any, was integrated into develop before it was archived.",
      };
  }
}

// ---------------------------------------------------------------------------
// Seeded jobs for each lifecycle state.
// ---------------------------------------------------------------------------
function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ASS-1752',
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
  } as TaskInfo;
}

interface Scenario {
  id: string;
  title: string;
  truth: string;
  lane: string;
  job: TaskInfo;
}

const SCENARIOS: readonly Scenario[] = [
  {
    id: 'A-active',
    title: 'State A — active task/<id> worktree (parallel run live)',
    truth: 'The run is live in its own task/ASS-1752 worktree; nothing merged yet.',
    lane: '3-progress',
    job: makeJob({
      state: TaskState.Progress,
      provenance: {
        branch: 'task/ASS-1752',
        base: 'base000',
        merge: null,
        transitions: [{ lane: TaskState.Progress, atUtc: '2026-06-10T10:00:00Z', branchTip: 'a1b2c3d', workBranchHead: 'a1b2c3d' }],
      },
    }),
  },
  {
    id: 'A-reissue',
    title: 'State A (reissue) — newest attempt wins',
    truth: 'First attempt cut a1b2c3d; the reissue re-ran and the live tip is now f9e8d7c.',
    lane: '3-progress',
    job: makeJob({
      state: TaskState.Progress,
      provenance: {
        branch: 'task/ASS-1752',
        base: 'base000',
        merge: null,
        transitions: [
          { lane: TaskState.Progress, atUtc: '2026-06-10T10:00:00Z', branchTip: 'a1b2c3d', workBranchHead: 'a1b2c3d' },
          { lane: TaskState.HumanReview, atUtc: '2026-06-10T10:30:00Z', branchTip: 'a1b2c3d', workBranchHead: 'a1b2c3d' },
          { lane: TaskState.Progress, atUtc: '2026-06-10T11:00:00Z', branchTip: 'f9e8d7c', workBranchHead: 'f9e8d7c' },
        ],
      },
    }),
  },
  {
    id: 'B-landed',
    title: 'State B — landed in develop (integrated + torn down)',
    truth: 'Parallel worktree was auto-merged at ddddddd and torn down; card sits in auto-review.',
    lane: '4-auto-review',
    job: makeJob({
      state: TaskState.AutoReview,
      provenance: {
        branch: 'task/ASS-1752',
        base: 'base000',
        transitions: [{ lane: TaskState.AutoReview, atUtc: '2026-06-10T10:30:00Z', branchTip: 'a1b2c3d', workBranchHead: 'a1b2c3d' }],
        merge: {
          mergeCommit: 'ddddddd9aa11',
          workBranchHeadBefore: 'dev0000',
          workBranchHeadAfter: 'ddddddd9aa11',
          atUtc: '2026-06-10T10:30:00Z',
        },
      },
    }),
  },
  {
    id: 'C-sequential',
    title: 'State C — sequential run, shared main checkout',
    truth: 'maxParallelism=1: the run worked directly in the shared checkout; no task branch was ever cut.',
    lane: '3-progress',
    job: makeJob({
      state: TaskState.Progress,
      provenance: {
        branch: 'task/ASS-1752',
        base: 'base000',
        merge: null,
        transitions: [{ lane: TaskState.Progress, atUtc: '2026-06-10T10:00:00Z', branchTip: null, workBranchHead: null }],
      },
    }),
  },
];

@Component({
  selector: 'mockup-git-state-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [],
  templateUrl: './git-state-gallery.component.html',
  styleUrl: './git-state-gallery.component.scss',
})
export class GitStateGalleryComponent {
  readonly scenarios = SCENARIOS;

  before(job: TaskInfo): GitStateBadge | null {
    return buildGitStateBadgeOld(job);
  }

  after(job: TaskInfo): GitStateBadge | null {
    return buildGitStateBadge(job);
  }
}
