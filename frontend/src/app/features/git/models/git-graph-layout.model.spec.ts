import { describe, expect, it } from 'vitest';
import { buildGitGraphRows } from './git-graph-layout.model';
import type { GitGraphCommit } from './git.model';

function commit(sha: string, parents: string[]): GitGraphCommit {
  return {
    sha,
    shortSha: sha,
    parentShas: parents,
    authorDateUtc: '2026-07-29T10:00:00Z',
    author: 'dev',
    subject: sha,
    filesChanged: 0,
    added: 0,
    removed: 0,
    refs: [],
    tasks: [],
    presence: null,
    deployments: [],
  };
}

describe('buildGitGraphRows', () => {
  it('keeps a linear history in one stable lane', () => {
    const rows = buildGitGraphRows([
      commit('c3', ['c2']),
      commit('c2', ['c1']),
      commit('c1', []),
    ]);

    expect(rows.map(row => row.lane)).toEqual([0, 0, 0]);
    expect(rows[0].segments[0]).toMatchObject({ x1: 8, x2: 8 });
  });

  it('opens a second lane for a merge parent and rejoins it by SHA', () => {
    const rows = buildGitGraphRows([
      commit('merge', ['main-parent', 'task-parent']),
      commit('main-parent', ['base']),
      commit('task-parent', ['base']),
      commit('base', []),
    ]);

    expect(rows[0].segments).toHaveLength(2);
    expect(rows[2].lane).toBe(1);
    expect(rows[3].lane).toBe(0);
    expect(rows.some(row => row.width > 16)).toBe(true);
  });
});
