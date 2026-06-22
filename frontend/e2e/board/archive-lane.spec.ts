import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, moveJob, listJobs } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

function uid() {
  return `e2e-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/** Delete all leftover e2e test jobs in the given watchPath. */
async function cleanupTestJobs(watchPath: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j =>
    j.watchPath === watchPath &&
    (j.id.startsWith('e2e-') ||
     j.id.startsWith('archive-') ||
     j.id.startsWith('review-'))
  );
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

test.describe('Archive lane', () => {
  test('Archive column heading is visible on the board', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Archive', exact: true })).toBeVisible({ timeout: 10_000 });
  });

  test('"Archive all" button appears in the Completed column header', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('archive-all-btn')).toBeVisible({ timeout: 10_000 });
  });

  test('"Archive all" moves only completed tasks to archive (leaves preparation/ready/review alone)', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    await cleanupTestJobs(watchPath);

    const idPrep = uid();
    const idReady = uid();
    const idReview = uid();
    const idDone = uid();
    const jobPrep = await createJob({ id: idPrep, title: `e2e-arch-prep-${idPrep}`, watchPath, targetState: '1-preparation' });
    const jobReady = await createJob({ id: idReady, title: `e2e-arch-ready-${idReady}`, watchPath, targetState: '2-ready' });
    const jobReview = await createJob({ id: idReview, title: `e2e-arch-review-${idReview}`, watchPath, targetState: '2-ready' });
    await moveJob(jobReview.id, watchPath, '4-review');
    const jobDone = await createJob({ id: idDone, title: `e2e-arch-done-${idDone}`, watchPath, targetState: '2-ready' });
    await moveJob(jobDone.id, watchPath, '4-review');
    await moveJob(jobDone.id, watchPath, '5-completed');

    try {
      await page.goto('/');
      const errorOverlay = page.locator('.overlay--error');
      if (await errorOverlay.isVisible({ timeout: 1_000 }).catch(() => false)) {
        await errorOverlay.click();
      }

      const archiveAllBtn = page.getByTestId('archive-all-btn');
      await expect(archiveAllBtn).toBeVisible({ timeout: 10_000 });
      await page.evaluate(() => {
        const el = document.querySelector('[data-testid="archive-all-btn"]') as HTMLElement;
        if (el) {
          el.scrollIntoView({ block: 'nearest', inline: 'center' });
          el.click();
        }
      });

      await page.waitForTimeout(2_000);

      const jobs = await listJobs();
      expect(jobs.find(j => j.id === jobDone.id)?.state).toBe('6-archive');
      expect(jobs.find(j => j.id === jobPrep.id)?.state).toBe('1-preparation');
      expect(jobs.find(j => j.id === jobReady.id)?.state).toBe('2-ready');
      expect(jobs.find(j => j.id === jobReview.id)?.state).toBe('4-review');
    } finally {
      await deleteJob(jobPrep.id, watchPath).catch(() => {});
      await deleteJob(jobReady.id, watchPath).catch(() => {});
      await deleteJob(jobReview.id, watchPath).catch(() => {});
      await deleteJob(jobDone.id, watchPath).catch(() => {});
    }
  });
});
