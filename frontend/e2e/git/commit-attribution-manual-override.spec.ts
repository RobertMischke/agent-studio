import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Commit-Attribution-Regel (ADR-0050) operator override round-trip.
 *
 * The deterministic engine binds commits automatically, but the operator
 * always gets the final say from the git pane: attach a recent commit the
 * engine never saw ("+ Add commit" -> manual-add), withhold one it picked
 * ("Exclude" -> manual-exclude), and restore a withheld one
 * (manual-include-after-exclude). This spec drives that full state machine
 * through the API (the source of truth the UI mutates) and then confirms the
 * git pane renders the override affordances.
 */

interface WatchPath { path: string; name?: string }

interface RecentCommit {
  sha: string;
  shortSha: string;
  subject: string;
  filesChanged: number;
}

interface JobCommitInfo {
  sha: string;
  attribution?: string;
  filesChanged?: number;
}

interface JobInfoShape {
  commits?: JobCommitInfo[];
  excludedCommits?: { sha: string; reason: string; manual?: boolean }[];
}

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => (p.name ?? '').toLowerCase().includes('agent task processor')) ?? paths[0];
}

const q = (wp: string) => `?watchPath=${encodeURIComponent(wp)}`;

async function readInfo(jobId: string, wp: string): Promise<JobInfoShape> {
  const detail = await api<{ info: JobInfoShape }>(`/api/jobs/${encodeURIComponent(jobId)}${q(wp)}`);
  return detail.info ?? {};
}

test.describe('Commit attribution — manual operator override', () => {
  test('add-from-recent, exclude, and restore drive the persisted attribution state', async () => {
    const wp = await pickWatchPath();
    const job = await createJob({
      title: `attr-override-${Date.now()}`,
      watchPath: wp.path,
      cliType: 'claude',
      agent: 'claude',
      targetState: '2-ready'
    });
    await api(`/api/jobs/${encodeURIComponent(job.id)}/move${q(wp.path)}`,
      { method: 'POST', body: JSON.stringify({ targetState: '3-progress' }) });

    try {
      // Pick a real branch commit the engine never attributed to this fresh job.
      const recent = await api<{ commits: RecentCommit[] }>(
        `/api/jobs/${encodeURIComponent(job.id)}/git/recent-commits${q(wp.path)}&limit=5`);
      expect(recent.commits.length).toBeGreaterThan(0);
      const target = recent.commits[0];

      // 1) "+ Add commit": attach it -> manual-add, enriched with a real file count.
      const added = await api<{ included: boolean }>(
        `/api/jobs/${encodeURIComponent(job.id)}/commits/${encodeURIComponent(target.sha)}/include${q(wp.path)}`,
        { method: 'POST', body: JSON.stringify({ message: target.subject }) });
      expect(added.included).toBe(true);

      let info = await readInfo(job.id, wp.path);
      let entry = info.commits?.find(c => c.sha === target.sha);
      expect(entry, 'added commit must appear in commits[]').toBeTruthy();
      expect(entry!.attribution).toBe('manual-add');
      expect(entry!.filesChanged ?? 0).toBeGreaterThan(0);

      // 2) Exclude it -> moves to excludedCommits with a manual marker.
      const excluded = await api<{ excluded: boolean }>(
        `/api/jobs/${encodeURIComponent(job.id)}/commits/${encodeURIComponent(target.sha)}/exclude${q(wp.path)}`,
        { method: 'POST' });
      expect(excluded.excluded).toBe(true);

      info = await readInfo(job.id, wp.path);
      expect(info.commits?.some(c => c.sha === target.sha)).toBeFalsy();
      const ex = info.excludedCommits?.find(e => e.sha === target.sha);
      expect(ex, 'excluded commit must appear in excludedCommits[]').toBeTruthy();
      expect(ex!.reason).toBe('manual-exclude');
      expect(ex!.manual).toBe(true);

      // 3) Restore it -> back into commits[] as manual-include-after-exclude.
      const restored = await api<{ included: boolean }>(
        `/api/jobs/${encodeURIComponent(job.id)}/commits/${encodeURIComponent(target.sha)}/include${q(wp.path)}`,
        { method: 'POST', body: JSON.stringify({ message: target.subject }) });
      expect(restored.included).toBe(true);

      info = await readInfo(job.id, wp.path);
      entry = info.commits?.find(c => c.sha === target.sha);
      expect(entry, 'restored commit must be back in commits[]').toBeTruthy();
      expect(entry!.attribution).toBe('manual-include-after-exclude');
      expect(info.excludedCommits?.some(e => e.sha === target.sha)).toBeFalsy();
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}${q(wp.path)}`, { method: 'DELETE' });
    }
  });

  test('git pane exposes the "+ Add commit" override affordance', async ({ page }) => {
    const wp = await pickWatchPath();
    const job = await createJob({
      title: `attr-override-ui-${Date.now()}`,
      watchPath: wp.path,
      cliType: 'claude',
      agent: 'claude',
      targetState: '2-ready'
    });
    await api(`/api/jobs/${encodeURIComponent(job.id)}/move${q(wp.path)}`,
      { method: 'POST', body: JSON.stringify({ targetState: '3-progress' }) });

    // Seed a commit so the git pane lands in its 'commit' view mode where the
    // attribution controls live.
    const recent = await api<{ commits: RecentCommit[] }>(
      `/api/jobs/${encodeURIComponent(job.id)}/git/recent-commits${q(wp.path)}&limit=5`);
    const target = recent.commits[0];
    await api(`/api/jobs/${encodeURIComponent(job.id)}/commits/${encodeURIComponent(target.sha)}/include${q(wp.path)}`,
      { method: 'POST', body: JSON.stringify({ message: target.subject }) });

    try {
      // Let the detail endpoint catch up with the create + move + seed.
      const deadline = Date.now() + 10_000;
      while (Date.now() < deadline) {
        try { await api(`/api/jobs/${encodeURIComponent(job.id)}${q(wp.path)}`); break; }
        catch { await new Promise(r => setTimeout(r, 200)); }
      }

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(wp.path)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

      // The operator override affordance is always present in commit view.
      const addToggle = page.getByTestId('git-add-commit-toggle');
      await expect(addToggle).toBeVisible({ timeout: 10_000 });

      // Opening the picker fetches recent commits and lists addable ones.
      await addToggle.click();
      await expect(page.getByTestId('git-add-commit-picker')).toBeVisible();

      await page.screenshot({ path: 'e2e/_baselines/commit-attribution-add-picker.png', fullPage: false });
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}${q(wp.path)}`, { method: 'DELETE' });
    }
  });
});
