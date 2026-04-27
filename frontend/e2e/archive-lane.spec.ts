import { test, expect } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, moveJob, listJobs } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
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

test.describe('"Complete & Next" in Review detail view', () => {
  test('"Complete & Next" button is visible when a review task is open', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    await cleanupTestJobs(watchPath);

    const id = uid();
    const job = await createJob({ id, title: `e2e-rev-btn-${id}`, watchPath, targetState: '2-ready' });
    await moveJob(job.id, watchPath, '4-review');

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      await expect(page.getByTestId('back-to-board')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('complete-and-next-btn')).toBeVisible({ timeout: 5_000 });
    } finally {
      await deleteJob(job.id, watchPath).catch(() => {});
    }
  });

  test('"Complete & Next" marks the current task completed and jumps to the next review task', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    await cleanupTestJobs(watchPath);

    const idA = uid();
    const idB = uid();
    const jobA = await createJob({ id: idA, title: `e2e-rev-next-A-${idA}`, watchPath, targetState: '2-ready' });
    const jobB = await createJob({ id: idB, title: `e2e-rev-next-B-${idB}`, watchPath, targetState: '2-ready' });
    await moveJob(jobA.id, watchPath, '4-review');
    await moveJob(jobB.id, watchPath, '4-review');

    // Also move all other review jobs away so the result is deterministic
    const existing = await listJobs();
    const otherReview = existing.filter(j =>
      j.state === '4-review' &&
      j.id !== jobA.id &&
      j.id !== jobB.id
    );
    for (const j of otherReview) {
      await moveJob(j.id, j.watchPath, '1-preparation').catch(() => {});
    }

    try {
      await page.goto(`/?job=${encodeURIComponent(jobA.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      await expect(page.getByTestId('back-to-board')).toBeVisible({ timeout: 10_000 });

      const completeBtn = page.getByTestId('complete-and-next-btn');
      await expect(completeBtn).toBeVisible({ timeout: 5_000 });
      await completeBtn.click();

      // The URL should change to jobB (still in review)
      await expect(page).toHaveURL(new RegExp(encodeURIComponent(jobB.id)), { timeout: 8_000 });

      // jobA should now be completed
      const jobs = await listJobs();
      expect(jobs.find(j => j.id === jobA.id)?.state).toBe('5-completed');
    } finally {
      await deleteJob(jobA.id, watchPath).catch(() => {});
      await deleteJob(jobB.id, watchPath).catch(() => {});
      for (const j of otherReview) {
        await moveJob(j.id, j.watchPath, '4-review').catch(() => {});
      }
    }
  });

  test('"Complete & Next" returns to board when no more review tasks remain', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    await cleanupTestJobs(watchPath);

    // Move ALL review jobs away
    const existing = await listJobs();
    const allReview = existing.filter(j => j.state === '4-review');
    for (const j of allReview) {
      await moveJob(j.id, j.watchPath, '1-preparation').catch(() => {});
    }

    const id = uid();
    const job = await createJob({ id, title: `e2e-rev-last-${id}`, watchPath, targetState: '2-ready' });
    await moveJob(job.id, watchPath, '4-review');

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      await expect(page.getByTestId('back-to-board')).toBeVisible({ timeout: 10_000 });

      const completeBtn = page.getByTestId('complete-and-next-btn');
      await expect(completeBtn).toBeVisible({ timeout: 5_000 });
      await completeBtn.click();

      // Should navigate back to board (URL no longer has job param)
      await expect(page).not.toHaveURL(/[?&]job=/, { timeout: 8_000 });
    } finally {
      await deleteJob(job.id, watchPath).catch(() => {});
      for (const j of allReview) {
        await moveJob(j.id, j.watchPath, '4-review').catch(() => {});
      }
    }
  });
});
