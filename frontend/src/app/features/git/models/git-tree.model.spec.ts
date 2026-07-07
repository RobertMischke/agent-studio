import { describe, expect, it } from 'vitest';
import { buildGitTree, branchCategoryLabel } from './git-tree.model';
import type {
  GitBranchEntry,
  GitCommitEntry,
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

function commit(sha: string): GitCommitEntry {
  return {
    sha,
    shortSha: sha.slice(0, 7),
    authorDateUtc: '2026-07-01T00:00:00Z',
    author: 'dev',
    subject: `commit ${sha.slice(0, 4)}`,
    filesChanged: 1,
    added: 1,
    removed: 0,
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
    // no "worktrees", no "history" (all empty).
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

  it('emits a history group with one node per recent commit', () => {
    const inv = inventory({
      recentCommits: [commit('1111111111111111111111111111111111111111'), commit('2222222222222222222222222222222222222222')],
    });

    const history = buildGitTree(inv).find(g => g.id === 'history')!;
    expect(history.count).toBe(2);
    expect(history.children.every(c => c.kind === 'commit')).toBe(true);
  });

  it('gives every node a stable unique id', () => {
    const inv = inventory({
      worktrees: [worktree({ path: 'C:/repo', isPrimary: true })],
      branches: [branch({ name: 'main', category: 'main' })],
      recentCommits: [commit('deadbeefdeadbeefdeadbeefdeadbeefdeadbeef')],
    });

    const ids = buildGitTree(inv).flatMap(g => g.children.map(c => c.id));
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toContain('branch:main');
    expect(ids).toContain('wt:C:/repo');
    expect(ids).toContain('commit:deadbeefdeadbeefdeadbeefdeadbeefdeadbeef');
  });
});

describe('branchCategoryLabel', () => {
  it('maps categories to short labels', () => {
    expect(branchCategoryLabel('main')).toBe('main');
    expect(branchCategoryLabel('develop')).toBe('develop');
    expect(branchCategoryLabel('feature')).toBe('feature');
    expect(branchCategoryLabel('task')).toBe('task');
    expect(branchCategoryLabel('other')).toBe('branch');
  });
});
