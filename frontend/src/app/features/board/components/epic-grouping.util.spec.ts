import { describe, expect, it } from 'vitest';
import { buildEpicGroups, flattenGrouped } from './epic-grouping.util';
import { GroupedJobs, TaskInfo } from '../../../models/task.model';

interface TaskOverrides {
  kind?: TaskInfo['kind'];
  epicId?: string | null;
  state?: string;
  order?: number;
  title?: string;
  projectName?: string;
}

function task(id: string, o: TaskOverrides = {}): TaskInfo {
  return {
    id,
    taskKey: `ws::${id}`,
    title: o.title ?? id,
    state: o.state ?? '0-backlog',
    order: o.order ?? 1,
    agent: 'claude',
    createdAt: '2026-05-05T12:00:00Z',
    watchPath: 'ws',
    projectName: o.projectName ?? 'demo',
    folderPath: '/tmp',
    lastActivity: '2026-05-05T12:00:00Z',
    kind: o.kind,
    epicId: o.epicId ?? null,
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
    review: [],
    completed: [],
    archive: [],
  };
}

describe('buildEpicGroups', () => {
  it('nests sub-tasks under their epic and rolls up progress', () => {
    const groups = buildEpicGroups([
      task('epic-1', { kind: 'epic', title: 'Ship feature' }),
      task('s1', { epicId: 'epic-1', state: '6-completed', order: 1 }),
      task('s2', { epicId: 'epic-1', state: '7-archive', order: 2 }),
      task('s3', { epicId: 'epic-1', state: '3-progress', order: 3 }),
      task('s4', { epicId: 'epic-1', state: '0-backlog', order: 4 }),
    ]);

    expect(groups).toHaveLength(1);
    const g = groups[0];
    expect(g.id).toBe('epic-1');
    expect(g.label).toBe('Ship feature');
    expect(g.epic?.id).toBe('epic-1');
    expect(g.subTasks.map((t) => t.id)).toEqual(['s1', 's2', 's3', 's4']);
    expect(g.total).toBe(4);
    expect(g.completed).toBe(2); // 6-completed + 7-archive
    expect(g.open).toBe(1); // 0-backlog (2-ready also counts as open)
    expect(g.inProgress).toBe(1); // total - completed - open
    expect(g.progressPct).toBe(50);
  });

  it('orders sub-tasks by board order then title', () => {
    const groups = buildEpicGroups([
      task('epic-1', { kind: 'epic' }),
      task('b', { epicId: 'epic-1', order: 2 }),
      task('a', { epicId: 'epic-1', order: 1 }),
      task('c', { epicId: 'epic-1', order: 1, title: 'aaa' }),
    ]);
    // order 1 group: c (title "aaa") before a (title "a")? localeCompare: "a" < "aaa"
    expect(groups[0].subTasks.map((t) => t.id)).toEqual(['a', 'c', 'b']);
  });

  it('collects tasks with no epic into the "No epic" group, last among real epics', () => {
    const groups = buildEpicGroups([
      task('loose-1'),
      task('epic-1', { kind: 'epic' }),
      task('s1', { epicId: 'epic-1' }),
      task('loose-2'),
    ]);
    expect(groups.map((g) => g.id)).toEqual(['epic-1', '__none__']);
    const none = groups[1];
    expect(none.label).toBe('No epic');
    expect(none.epic).toBeNull();
    expect(none.subTasks.map((t) => t.id).sort()).toEqual(['loose-1', 'loose-2']);
  });

  it('routes sub-tasks whose epic is absent into the orphan bucket, after everything else', () => {
    const groups = buildEpicGroups([
      task('epic-1', { kind: 'epic' }),
      task('s1', { epicId: 'epic-1' }),
      task('orphan-1', { epicId: 'missing-epic' }),
    ]);
    expect(groups.map((g) => g.id)).toEqual(['epic-1', '__orphan__']);
    const orphan = groups[1];
    expect(orphan.label).toBe('Orphaned sub-tasks');
    expect(orphan.subTasks.map((t) => t.id)).toEqual(['orphan-1']);
  });

  it('omits synthetic groups when empty', () => {
    const groups = buildEpicGroups([
      task('epic-1', { kind: 'epic' }),
      task('s1', { epicId: 'epic-1' }),
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0].id).toBe('epic-1');
  });

  it('reports 0% for an epic with no sub-tasks', () => {
    const groups = buildEpicGroups([task('epic-1', { kind: 'epic' })]);
    expect(groups[0].total).toBe(0);
    expect(groups[0].progressPct).toBe(0);
    expect(groups[0].inProgress).toBe(0);
  });

  it('sorts epics by project then order', () => {
    const groups = buildEpicGroups([
      task('e-z', { kind: 'epic', projectName: 'zeta', order: 1 }),
      task('e-a2', { kind: 'epic', projectName: 'alpha', order: 2 }),
      task('e-a1', { kind: 'epic', projectName: 'alpha', order: 1 }),
    ]);
    expect(groups.map((g) => g.id)).toEqual(['e-a1', 'e-a2', 'e-z']);
  });
});

describe('flattenGrouped', () => {
  it('de-duplicates the auto-review / review legacy alias', () => {
    const grouped = emptyGrouped();
    const card = task('dup', { state: '4-auto-review' });
    grouped.autoReview = [card];
    grouped.review = [card]; // legacy alias points at the same card

    const flat = flattenGrouped(grouped);
    expect(flat.map((t) => t.id)).toEqual(['dup']);
  });

  it('collects across every lane', () => {
    const grouped = emptyGrouped();
    grouped.backlog = [task('a')];
    grouped.progress = [task('b')];
    grouped.completed = [task('c')];

    const flat = flattenGrouped(grouped);
    expect(flat.map((t) => t.id).sort()).toEqual(['a', 'b', 'c']);
  });
});
