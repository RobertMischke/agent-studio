/**
 * Pure tree model for the Project Hub Git View. Transforms the flat backend
 * {@link GitProjectInventory} into the grouped, ordered node list the tree
 * browser renders on the left pane: worktrees first, then branches grouped by
 * category (integration / feature / task / runner / other).
 *
 * Kept free of Angular so the grouping/ordering logic is unit-tested in
 * isolation and the component stays a thin renderer over `buildGitTree`.
 */
import type {
  GitActiveCheckout,
  GitBranchEntry,
  GitProjectInventory,
  GitWorktreeEntry,
} from './git.model';

export type GitTreeGroupId =
  | 'active'
  | 'worktrees'
  | 'integration'
  | 'feature'
  | 'task'
  | 'runner'
  | 'other';

export interface GitTreeBranchNode {
  readonly kind: 'branch';
  readonly id: string;
  readonly branch: GitBranchEntry;
}

export interface GitTreeWorktreeNode {
  readonly kind: 'worktree';
  readonly id: string;
  readonly worktree: GitWorktreeEntry;
}

export interface GitTreeActiveNode {
  readonly kind: 'active';
  readonly id: string;
  readonly checkout: GitActiveCheckout;
}

export type GitTreeLeaf = GitTreeBranchNode | GitTreeWorktreeNode | GitTreeActiveNode;

export interface GitTreeGroup {
  readonly kind: 'group';
  readonly id: GitTreeGroupId;
  readonly label: string;
  readonly count: number;
  readonly children: readonly GitTreeLeaf[];
}

const GROUP_LABELS: Record<GitTreeGroupId, string> = {
  active: 'In progress',
  worktrees: 'Worktrees & checkouts',
  integration: 'Integration branches',
  feature: 'Feature branches',
  task: 'Task branches',
  runner: 'Runner branches',
  other: 'Other branches',
};

/**
 * Build the grouped tree for the Git View. Empty groups are omitted so the
 * tree never shows a dangling header with no rows. Branch ordering inside each
 * group preserves the backend's recency sort; the current branch is floated to
 * the top of its group so "where am I" is always the first row.
 */
export function buildGitTree(inventory: GitProjectInventory | null): GitTreeGroup[] {
  if (!inventory || !inventory.isRepo) return [];

  const groups: GitTreeGroup[] = [];

  const active = inventory.activeCheckouts ?? [];
  if (active.length > 0) {
    groups.push(group('active', active.map((checkout): GitTreeActiveNode => ({
      kind: 'active',
      id: `active:${checkout.task.taskKey}`,
      checkout,
    }))));
  }

  const worktrees = [...(inventory.worktrees ?? [])].sort(sortWorktrees);
  if (worktrees.length > 0) {
    groups.push(group('worktrees', worktrees.map((w): GitTreeWorktreeNode => ({
      kind: 'worktree',
      id: `wt:${w.path}`,
      worktree: w,
    }))));
  }

  const branches = inventory.branches ?? [];
  pushBranchGroup(groups, 'integration', branches.filter(b => b.category === 'main' || b.category === 'develop'));
  pushBranchGroup(groups, 'feature', branches.filter(b => b.category === 'feature'));
  pushBranchGroup(groups, 'task', branches.filter(b => b.category === 'task'));
  pushBranchGroup(groups, 'runner', branches.filter(b => b.category === 'runner'));
  pushBranchGroup(groups, 'other', branches.filter(b => b.category === 'other'));

  return groups;
}

function pushBranchGroup(groups: GitTreeGroup[], id: GitTreeGroupId, entries: GitBranchEntry[]): void {
  if (entries.length === 0) return;
  const ordered = [...entries].sort(currentFirst);
  groups.push(group(id, ordered.map((b): GitTreeBranchNode => ({
    kind: 'branch',
    id: `branch:${b.name}`,
    branch: b,
  }))));
}

function group(id: GitTreeGroupId, children: readonly GitTreeLeaf[]): GitTreeGroup {
  return { kind: 'group', id, label: GROUP_LABELS[id], count: children.length, children };
}

/** Primary checkout first, then by path for a stable order. */
function sortWorktrees(a: GitWorktreeEntry, b: GitWorktreeEntry): number {
  if (a.isPrimary !== b.isPrimary) return a.isPrimary ? -1 : 1;
  return a.path.localeCompare(b.path);
}

/** Keep the checked-out branch at the top of its group; otherwise preserve order. */
function currentFirst(a: GitBranchEntry, b: GitBranchEntry): number {
  if (a.isCurrent !== b.isCurrent) return a.isCurrent ? -1 : 1;
  return 0;
}

/** Short display label for a branch category, used by the tree row badge. */
export function branchCategoryLabel(category: GitBranchEntry['category']): string {
  switch (category) {
    case 'main': return 'main';
    case 'develop': return 'develop';
    case 'feature': return 'feature';
    case 'task': return 'task';
    case 'runner': return 'runner';
    default: return 'branch';
  }
}
