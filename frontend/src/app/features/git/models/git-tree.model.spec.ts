import { describe, expect, it } from 'vitest';
import { buildGitTree, branchCategoryLabel } from './git-tree.model';
import type {
  GitActiveCheckout,
  GitBranchEntry,
  GitProjectInventory,
  GitWorktreeEntry,
} from './git.model';

function branch(partial: Partial<GitBranchEntry> & Pick<GitBranchEntry, 'name' | 'category'>): GitBranchEntry {
  return {
    tipSha: 'a'.repeat(40),
    tipShortSha: 'aaaaaaa',
    isCurrent: false,
    upstream: null,
    ahead: 0,
    behind: 0,
    lastCommitSubject: 'subject',
    lastCommitAtUtc: '2026-07-01T00:00:00Z',
    worktreePath: null,
    ...partial,
  };
}

function worktree(partial: Partial<GitWorktreeEntry> & Pick<GitWorktreeEntry, 'path'>): GitWorktreeEntry {
  return {
    branch: null,
    headSha: 'b'.repeat(40),
    headShortSha: 'bbbbbbb',
    isPrimary: false,
    isDetached: false,
    isBare: false,
    ...partial,
  };
}

function inventory(partial: Partial<GitProjectInventory>): GitProjectInventory {
  return {
    projectName: 'Demo',
    repositoryPath: 'C:/repo',
    isRepo: true,
    currentBranch: 'main',
    worktrees: [],
    branches: [],
    recentCommits: [],
    error: null,
    ...partial,
  };
}

describe('buildGitTree', () => {
  it('returns no groups when the inventory is missing or not a repo', () => {
    expect(buildGitTree(null)).toEqual([]);
    expect(buildGitTree(inventory({ isRepo: false, error: 'Not a git repository' }))).toEqual([]);
  });

  it('groups branches by category and omits empty groups', () => {
    const inv = inventory({
      branches: [
        branch({ name: 'main', category: 'main', isCurrent: true }),
        branch({ name: 'develop', category: 'develop' }),
        branch({ name: 'feature/login', category: 'feature' }),
        branch({ name: 'task/42', category: 'task' }),
        branch({ name: 'task/43', category: 'task' }),
      ],
    });

    const tree = buildGitTree(inv);
    const ids = tree.map(g => g.id);

    // Integration (main+develop), feature, task groups present; no "other",
    // no "worktrees", "runner", or "active" group (all empty).
    expect(ids).toEqual(['integration', 'feature', 'task']);

    const integration = tree.find(g => g.id === 'integration')!;
    expect(integration.count).toBe(2);
    expect(integration.children.map(c => c.kind)).toEqual(['branch', 'branch']);

    const task = tree.find(g => g.id === 'task')!;
    expect(task.count).toBe(2);
  });

  it('floats the current branch to the top of its group', () => {
    const inv = inventory({
      branches: [
        branch({ name: 'feature/a', category: 'feature' }),
        branch({ name: 'feature/b', category: 'feature', isCurrent: true }),
        branch({ name: 'feature/c', category: 'feature' }),
      ],
    });

    const feature = buildGitTree(inv).find(g => g.id === 'feature')!;
    const first = feature.children[0];
    expect(first.kind).toBe('branch');
    expect((first as { branch: GitBranchEntry }).branch.name).toBe('feature/b');
  });

  it('orders worktrees with the primary checkout first', () => {
    const inv = inventory({
      worktrees: [
        worktree({ path: 'C:/repo/wt-task', branch: 'task/42' }),
        worktree({ path: 'C:/repo', isPrimary: true, branch: 'main' }),
      ],
    });

    const group = buildGitTree(inv).find(g => g.id === 'worktrees')!;
    expect(group.children).toHaveLength(2);
    const firstNode = group.children[0] as { worktree: GitWorktreeEntry };
    expect(firstNode.worktree.isPrimary).toBe(true);
    expect(firstNode.worktree.path).toBe('C:/repo');
  });

  it('puts active leases before worktrees and keeps their stable task identity', () => {
    const checkout: GitActiveCheckout = {
      task: { taskKey: 'Demo::42', key: 'AGT-42', title: 'Tree', lane: '3-progress' },
      branch: 'runner/host/AGT-42',
      headSha: 'c'.repeat(40),
      location: 'remote',
      runner: 'agent-runner-01',
      worktreePath: null,
      activeSince: '2026-07-29T10:00:00Z',
    };
    const inv = inventory({
      activeCheckouts: [checkout],
      worktrees: [worktree({ path: 'C:/repo', isPrimary: true })],
    });

    const tree = buildGitTree(inv);
    expect(tree.map(group => group.id)).toEqual(['active', 'worktrees']);
    expect(tree[0].children[0]).toMatchObject({
      kind: 'active',
      id: 'active:Demo::42',
    });
  });

  it('gives every node a stable unique id', () => {
    const inv = inventory({
      worktrees: [worktree({ path: 'C:/repo', isPrimary: true })],
      branches: [
        branch({ name: 'main', category: 'main' }),
        branch({ name: 'runner/host/AGT-42', category: 'runner' }),
      ],
    });

    const ids = buildGitTree(inv).flatMap(g => g.children.map(c => c.id));
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toContain('branch:main');
    expect(ids).toContain('branch:runner/host/AGT-42');
    expect(ids).toContain('wt:C:/repo');
  });
});

describe('branchCategoryLabel', () => {
  it('maps categories to short labels', () => {
    expect(branchCategoryLabel('main')).toBe('main');
    expect(branchCategoryLabel('develop')).toBe('develop');
    expect(branchCategoryLabel('feature')).toBe('feature');
    expect(branchCategoryLabel('task')).toBe('task');
    expect(branchCategoryLabel('runner')).toBe('runner');
    expect(branchCategoryLabel('other')).toBe('branch');
  });
});
