import { describe, expect, it } from 'vitest';
import type { GitGraphCommit } from './git.model';
import { buildGitCommitChips } from './git-commit-chip.model';

function commit(overrides: Partial<GitGraphCommit> = {}): GitGraphCommit {
  return {
    sha: 'a'.repeat(40),
    shortSha: 'aaaaaaa',
    parentShas: [],
    authorDateUtc: '2026-07-31T10:00:00Z',
    author: 'dev',
    subject: 'feat: quiet commit chips',
    filesChanged: 1,
    added: 2,
    removed: 0,
    refs: [],
    tasks: [],
    presence: null,
    deployments: [],
    ...overrides,
  };
}

describe('buildGitCommitChips', () => {
  it('uses one ordered vocabulary without rendering raw ref names', () => {
    const chips = buildGitCommitChips(commit({
      refs: [
        { name: 'develop', kind: 'branch', isRemote: false },
        { name: 'origin/develop', kind: 'branch', isRemote: true },
      ],
      tasks: [{ taskKey: 'demo::agt-1', key: 'AGT-1', title: 'Quiet chips', lane: '3-progress' }],
      presence: {
        inIntegration: true,
        inRelease: false,
        integrationBranch: 'develop',
        releaseBranch: 'main',
      },
      deployments: [{ target: 'runner', sha: 'a'.repeat(40), shortSha: 'aaaaaaa' }],
    }));

    expect(chips.map(chip => chip.label)).toEqual([
      'Integrated · develop',
      'Deployed · runner',
      'AGT-1',
      'In progress',
      'Remote',
    ]);
    expect(chips.some(chip => chip.label === 'develop' || chip.label === 'origin/develop')).toBe(false);
    expect(chips.at(-1)?.detail).toContain('origin/develop');
  });

  it('shows release presence only when the commit is contained in main', () => {
    const chips = buildGitCommitChips(commit({
      presence: {
        inIntegration: true,
        inRelease: true,
        integrationBranch: 'develop',
        releaseBranch: 'main',
      },
    }));

    expect(chips.map(chip => chip.label)).toEqual([
      'Integrated · develop',
      'Released · main',
    ]);
  });
});
