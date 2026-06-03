import { GroupedJobs, TaskInfo } from '../../../models/task.model';

/**
 * Group-by-epic board view model. The "Gruppieren nach Epic" toggle renders
 * the flat lane feed as a tree: one section per epic (the epic card plus the
 * sub-tasks whose `epicId` points at it), a catch-all for ordinary tasks with
 * no epic, and an orphan bucket for sub-tasks whose epic is not on the board
 * (filtered out, or in another project). Pure so it stays testable without
 * Angular's TestBed - mirrors the `review-grouping.util` pattern.
 *
 * Progress bucketing mirrors the backend `EpicEndpoints.BuildRollup` exactly so
 * the tree's "3 / 7" matches `GET /api/epics`:
 *   completed  = 6-completed + 7-archive
 *   open       = 0-backlog   + 2-ready
 *   inProgress = total - completed - open
 */
export interface EpicGroupView {
  /** Stable group id: the epic id, or the `__none__` / `__orphan__` sentinels. */
  readonly id: string;
  /** Group header label. Epic title, or a fixed label for the synthetic groups. */
  readonly label: string;
  /** The epic card itself, or null for the synthetic (no-epic / orphan) groups. */
  readonly epic: TaskInfo | null;
  /** Sub-tasks under this group, ordered by `order` then title. */
  readonly subTasks: TaskInfo[];
  readonly total: number;
  readonly completed: number;
  readonly inProgress: number;
  readonly open: number;
  /** Completed share, 0-100, for the header progress bar. */
  readonly progressPct: number;
}

const NO_EPIC_ID = '__none__';
const ORPHAN_ID = '__orphan__';

const COMPLETED_STATES = new Set(['6-completed', '7-archive']);
const OPEN_STATES = new Set(['0-backlog', '2-ready']);

function isEpic(t: TaskInfo): boolean {
  return t.kind === 'epic';
}

function byOrderThenTitle(a: TaskInfo, b: TaskInfo): number {
  if (a.order !== b.order) return a.order - b.order;
  return a.title.localeCompare(b.title);
}

function rollup(subTasks: TaskInfo[]): {
  completed: number;
  inProgress: number;
  open: number;
  progressPct: number;
} {
  const total = subTasks.length;
  let completed = 0;
  let open = 0;
  for (const t of subTasks) {
    if (COMPLETED_STATES.has(t.state)) completed++;
    else if (OPEN_STATES.has(t.state)) open++;
  }
  const inProgress = total - completed - open;
  const progressPct = total === 0 ? 0 : Math.round((completed / total) * 100);
  return { completed, inProgress, open, progressPct };
}

/**
 * Flatten the lane-keyed grouped feed into a single de-duplicated task list.
 * `GroupedJobs.review` is a legacy alias for `autoReview`, so a naive concat
 * would double-count every auto-review card; we dedupe by `taskKey` to collapse
 * those (and any other accidental cross-lane repeat).
 */
export function flattenGrouped(grouped: GroupedJobs): TaskInfo[] {
  const lanes: TaskInfo[][] = [
    grouped.backlog,
    grouped.preparation,
    grouped.orchestratorPrep,
    grouped.ready,
    grouped.progress,
    grouped.failedPickup,
    grouped.autoReview,
    grouped.humanReview,
    grouped.review,
    grouped.completed,
    grouped.archive,
  ];
  const seen = new Set<string>();
  const out: TaskInfo[] = [];
  for (const lane of lanes) {
    if (!lane) continue;
    for (const t of lane) {
      if (seen.has(t.taskKey)) continue;
      seen.add(t.taskKey);
      out.push(t);
    }
  }
  return out;
}

/**
 * Build the epic tree from a flat task list. Order: epics first (by project,
 * then board order, then title), then the "No epic" catch-all, then the orphan
 * bucket. Synthetic groups are omitted when empty so a board with no orphans
 * doesn't show an empty section.
 */
export function buildEpicGroups(tasks: readonly TaskInfo[]): EpicGroupView[] {
  const epics: TaskInfo[] = [];
  const subTasksByEpic = new Map<string, TaskInfo[]>();
  const ungrouped: TaskInfo[] = [];

  for (const t of tasks) {
    if (isEpic(t)) {
      epics.push(t);
      continue;
    }
    if (t.epicId) {
      const list = subTasksByEpic.get(t.epicId);
      if (list) list.push(t);
      else subTasksByEpic.set(t.epicId, [t]);
      continue;
    }
    ungrouped.push(t);
  }

  const epicIds = new Set(epics.map((e) => e.id));
  const groups: EpicGroupView[] = [];

  const sortedEpics = [...epics].sort((a, b) => {
    if (a.projectName !== b.projectName) return a.projectName.localeCompare(b.projectName);
    return byOrderThenTitle(a, b);
  });

  for (const epic of sortedEpics) {
    const subTasks = (subTasksByEpic.get(epic.id) ?? []).slice().sort(byOrderThenTitle);
    groups.push({
      id: epic.id,
      label: epic.title,
      epic,
      subTasks,
      total: subTasks.length,
      ...rollup(subTasks),
    });
  }

  if (ungrouped.length > 0) {
    const subTasks = ungrouped.slice().sort(byOrderThenTitle);
    groups.push({
      id: NO_EPIC_ID,
      label: 'No epic',
      epic: null,
      subTasks,
      total: subTasks.length,
      ...rollup(subTasks),
    });
  }

  const orphans: TaskInfo[] = [];
  for (const [epicId, list] of subTasksByEpic) {
    if (!epicIds.has(epicId)) orphans.push(...list);
  }
  if (orphans.length > 0) {
    const subTasks = orphans.slice().sort(byOrderThenTitle);
    groups.push({
      id: ORPHAN_ID,
      label: 'Orphaned sub-tasks',
      epic: null,
      subTasks,
      total: subTasks.length,
      ...rollup(subTasks),
    });
  }

  return groups;
}
