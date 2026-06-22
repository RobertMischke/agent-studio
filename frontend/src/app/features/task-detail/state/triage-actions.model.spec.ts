import { describe, expect, it } from 'vitest';
import { mergeAcceptViewFor, overflowActionsFor, primaryActionFor } from './triage-actions.model';
import { TaskState } from '../../../models/task.model';
import type { TaskInfo } from '../../../models/task.model';
import type { LandedState, TaskProvenanceRecord } from '../../../features/git';

function reviewJob(provenance: TaskProvenanceRecord | null = null): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ATP-1',
    title: 'Task 1',
    state: TaskState.HumanReview,
    order: 1,
    agent: 'codex',
    createdAt: '2026-06-10T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/5-human-review/task-1',
    lastActivity: '2026-06-10T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    provenance,
  };
}

function mergedProvenance(mergeCommit: string | null): TaskProvenanceRecord {
  return {
    branch: 'task/task-1',
    base: 'base000',
    transitions: [],
    merge: mergeCommit
      ? { mergeCommit, workBranchHeadBefore: 'dev0000', workBranchHeadAfter: mergeCommit, atUtc: '2026-06-10T10:30:00Z' }
      : null,
  };
}

function overflowIds(state: string): string[] {
  return overflowActionsFor(state).map(b => b.id);
}

describe('primaryActionFor — Enter-bound primary per source lane', () => {
  it('labels the Completed lane primary "Archive & Next" and moves to 7-archive', () => {
    const primary = primaryActionFor('6-completed');
    expect(primary).not.toBeNull();
    expect(primary!.id).toBe('archive');
    expect(primary!.label).toBe('Archive & Next');
    expect(primary!.intent).toEqual({ kind: 'move', targetState: '7-archive' });
  });

  it('labels the Review lane primary "Merge into Develop" (→ 6-completed acceptance signal)', () => {
    const primary = primaryActionFor('5-human-review');
    expect(primary).not.toBeNull();
    expect(primary!.id).toBe('mark-done');
    expect(primary!.label).toBe('Merge into Develop');
    expect(primary!.intent).toEqual({ kind: 'move', targetState: '6-completed' });
  });

  it('leaves the Post Processing lane without a primary (Enter is a no-op)', () => {
    expect(primaryActionFor('4-auto-review')).toBeNull();
  });
});

describe('overflowActionsFor — Move to Completed / Move to Archive', () => {
  it('offers both moves from Ready, next to Send to Backlog and before Edit/Delete', () => {
    const ids = overflowIds('2-ready');
    expect(ids).toEqual([
      'move-to-top',
      'send-to-backlog',
      'move-to-completed',
      'move-to-archive',
      'edit-prompt',
      'delete',
    ]);
  });

  it('offers both moves from Backlog', () => {
    const ids = overflowIds('0-backlog');
    expect(ids).toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
    // Move entries precede the Edit/Delete safety nets.
    expect(ids.indexOf('move-to-completed')).toBeLessThan(ids.indexOf('edit-prompt'));
    expect(ids.indexOf('move-to-archive')).toBeLessThan(ids.indexOf('delete'));
  });

  it('offers both moves from a generic lane that has neither target (preparation)', () => {
    const ids = overflowIds('1-preparation');
    expect(ids).toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
  });

  it('skips Move to Completed when the lane already routes to 6-completed', () => {
    // 5-human-review's primary "Merge into Develop" targets 6-completed, so the
    // overflow must not add a duplicate. Move to Archive is still offered.
    const ids = overflowIds('5-human-review');
    expect(ids).not.toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
  });

  it('skips Move to Archive when the lane already routes to 7-archive', () => {
    // 6-completed's primary "Archive" targets 7-archive. Move to Completed is
    // suppressed too because 6-completed is the current lane.
    const ids = overflowIds('6-completed');
    expect(ids).not.toContain('move-to-archive');
    expect(ids).not.toContain('move-to-completed');
  });

  it('suppresses the current-lane target (no self-move from Archive)', () => {
    const ids = overflowIds('7-archive');
    expect(ids).not.toContain('move-to-archive');
    // Moving an archived card forward to Completed is still allowed.
    expect(ids).toContain('move-to-completed');
  });
});

describe('mergeAcceptViewFor — state-dependent Human Review acceptance primary', () => {
  it('keeps the "Merge into Develop" offer when nothing has landed yet', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance(null)));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
    expect(view.statusLabel).toBeNull();
    expect(view.landedState).toBe('on-branch-only');
  });

  it('treats a recorded merge fact as landed and relabels to "Accept"', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance('ddddddd9abc')));
    expect(view.landed).toBe(true);
    expect(view.landedState).toBe('merged-to-develop');
    expect(view.acceptLabel).toBe('Accept');
    expect(view.statusLabel).toBe('Merged to develop @ddddddd');
    expect(view.statusTooltip).toContain('already merged into develop at ddddddd');
  });

  it('upgrades wording to "Released to main" from the live landed-state hint', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance('ddddddd9')), 'released-to-main');
    expect(view.landed).toBe(true);
    expect(view.landedState).toBe('released-to-main');
    expect(view.acceptLabel).toBe('Accept');
    expect(view.statusLabel).toBe('Released to main');
  });

  it('lands purely on the live hint when no merge fact is persisted yet', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance(null)), 'merged-to-develop');
    expect(view.landed).toBe(true);
    expect(view.acceptLabel).toBe('Accept');
    expect(view.statusLabel).toBe('Merged to develop');
  });

  it('never lets a stale on-branch-only hint mask a recorded merge fact', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance('ddddddd9')), 'on-branch-only');
    expect(view.landed).toBe(true);
    expect(view.landedState).toBe('merged-to-develop');
  });

  it('ignores a blank/whitespace merge commit string', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance('   ')));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
  });

  it('stays an offer for a legacy card with no provenance at all', () => {
    const view = mergeAcceptViewFor(reviewJob(null));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
  });
});
