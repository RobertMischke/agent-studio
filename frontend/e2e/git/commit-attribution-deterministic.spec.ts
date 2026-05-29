import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Commit-Attribution-Regel (ADR-0050): the rule engine is deterministic -
 * the same git state always yields the same attribution, with no LLM on the
 * default path. The pure engine itself is unit-tested in
 * `backend.Tests` (CommitAttributionServiceTests); this E2E pins the two
 * read surfaces the UI depends on and asserts they are stable across calls:
 *
 *  - GET /git/recent-commits  (the "+ Add commit" override picker source)
 *  - GET /commits             (the attributed aggregate the git pane renders)
 *
 * Both must return byte-stable ordering for identical inputs, otherwise the
 * "deterministic" promise in the ADR is observable-broken from the frontend.
 */

interface WatchPath { path: string; name?: string }

interface RecentCommit {
  sha: string;
  shortSha: string;
  authorDateUtc: string;
  author: string;
  subject: string;
  filesChanged: number;
}

interface JobCommitInfo {
  sha: string;
  shortSha: string;
  attribution?: string;
  confidence?: number;
}

interface CommitsAggregate {
  commits: JobCommitInfo[];
  excludedCommits?: { sha: string; reason: string }[];
}

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => (p.name ?? '').toLowerCase().includes('agent task processor')) ?? paths[0];
}

const recentUrl = (jobId: string, wp: string, limit = 10) =>
  `/api/jobs/${encodeURIComponent(jobId)}/git/recent-commits?watchPath=${encodeURIComponent(wp)}&limit=${limit}`;

const commitsUrl = (jobId: string, wp: string) =>
  `/api/jobs/${encodeURIComponent(jobId)}/commits?watchPath=${encodeURIComponent(wp)}`;

test.describe('Commit attribution — deterministic read surfaces', () => {
  test('recent-commits and the attributed aggregate are stable across identical calls', async () => {
    const wp = await pickWatchPath();
    const job = await createJob({
      title: `attr-determinism-${Date.now()}`,
      watchPath: wp.path,
      cliType: 'claude',
      agent: 'claude',
      targetState: '2-ready'
    });
    await api(`/api/jobs/${encodeURIComponent(job.id)}/move?watchPath=${encodeURIComponent(wp.path)}`,
      { method: 'POST', body: JSON.stringify({ targetState: '3-progress' }) });

    try {
      // The host repo always has history, so the picker source is non-empty.
      const first = await api<{ commits: RecentCommit[] }>(recentUrl(job.id, wp.path));
      const second = await api<{ commits: RecentCommit[] }>(recentUrl(job.id, wp.path));

      expect(first.commits.length).toBeGreaterThan(0);
      // Determinism: same SHAs, same order, on a repeated call.
      expect(second.commits.map(c => c.sha)).toEqual(first.commits.map(c => c.sha));

      // Each candidate carries the shape the override picker renders.
      for (const c of first.commits) {
        expect(c.sha).toMatch(/^[0-9a-f]{7,40}$/);
        expect(typeof c.shortSha).toBe('string');
        expect(typeof c.subject).toBe('string');
        expect(typeof c.filesChanged).toBe('number');
      }

      // The attributed aggregate: a fresh 3-progress job has no agent run yet,
      // so the engine attributes nothing. The contract is that the arrays are
      // present and stable, not that they are populated.
      const aggA = await api<CommitsAggregate>(commitsUrl(job.id, wp.path));
      const aggB = await api<CommitsAggregate>(commitsUrl(job.id, wp.path));
      expect(Array.isArray(aggA.commits)).toBe(true);
      expect(aggB.commits.map(c => c.sha)).toEqual(aggA.commits.map(c => c.sha));

      // Any commit the engine *did* attribute automatically must carry a
      // confidence in [0,1]; manual entries never appear from a read-only path.
      for (const c of aggA.commits) {
        if (c.attribution === 'automatic') {
          expect(c.confidence).toBeGreaterThanOrEqual(0);
          expect(c.confidence).toBeLessThanOrEqual(1);
        }
      }
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    }
  });
});
