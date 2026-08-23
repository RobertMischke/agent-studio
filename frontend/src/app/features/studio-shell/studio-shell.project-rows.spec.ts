import { describe, expect, it } from 'vitest';
import { buildProjectSidebarRows } from './studio-shell.project-rows';
import { excludeEpics } from '../board';
import type { GroupedJobs, TaskInfo } from '../../models/task.model';

interface TaskOverrides {
  kind?: TaskInfo['kind'];
  projectName?: string;
}

function task(id: string, state: string, o: TaskOverrides = {}): TaskInfo {
  return {
    id,
    taskKey: `ws::${id}`,
    title: id,
    state,
    order: 1,
    agent: 'claude',
    createdAt: '2026-05-05T12:00:00Z',
    watchPath: 'ws',
    projectName: o.projectName ?? 'demo',
    folderPath: '/tmp',
    lastActivity: '2026-05-05T12:00:00Z',
    kind: o.kind,
    epicId: null,
  } as TaskInfo;
}

function emptyGrouped(): GroupedJobs {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    review: [],
    completed: [],
    archive: [],
  };
}

describe('buildProjectSidebarRows', () => {
  it('counts every task in ready / progress / human-review, including epics, when fed the raw grouped feed', () => {
    const grouped = emptyGrouped();
    grouped.ready = [task('t1', '2-ready')];
    grouped.progress = [task('t2', '3-progress')];
    grouped.humanReview = [task('t3', '5-human-review'), task('TE-8', '5-human-review', { kind: 'epic' })];

    const [row] = buildProjectSidebarRows(grouped, ['demo'], null);
    expect(row.laneCounts).toEqual({ ready: 1, progress: 1, humanReview: 2 });
  });

  it('matches the board (excludeEpics-filtered) lane counts once the same filter is applied - the AGT-2676 tree/board parity contract', () => {
    // Fixture mirrors the reported Token Economy sighting: an epic (TE-8) sits
    // in 5-human-review alongside an ordinary task.
    const grouped = emptyGrouped();
    grouped.ready = [task('r1', '2-ready'), task('EPIC-READY', '2-ready', { kind: 'epic' })];
    grouped.progress = [task('p1', '3-progress')];
    grouped.humanReview = [task('h1', '5-human-review')];
    grouped.escalated = [task('TE-8', '5e-escalated', { kind: 'epic' })];

    // `excludeEpics` is the exact filter the board applies to `filteredGrouped()`
    // before rendering lane columns (see app.ts `displayGrouped`). The tree must
    // be fed through the same filter so its dot count equals what the board
    // lanes actually render - never more, because of a hidden epic card.
    const boardVisibleGrouped = excludeEpics(grouped);
    const [row] = buildProjectSidebarRows(boardVisibleGrouped, ['demo'], null);

    const boardVisibleLaneCount =
      boardVisibleGrouped.ready.length + boardVisibleGrouped.progress.length + boardVisibleGrouped.humanReview.length + boardVisibleGrouped.escalated.length;

    expect(row.laneCounts).toEqual({ ready: 1, progress: 1, humanReview: 1 });
    expect(row.laneCounts.ready + row.laneCounts.progress + row.laneCounts.humanReview).toBe(boardVisibleLaneCount);
  });
});
