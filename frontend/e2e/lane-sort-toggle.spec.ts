import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

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

async function cleanup(prefix: string, watchPath: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

async function clearLaneSortStorage(page: Page): Promise<void> {
  await page.addInitScript(() => {
    try { localStorage.removeItem('laneSortMode'); } catch { /* ignore */ }
  });
}

/** Read job-card titles inside a column identified by its visible heading. */
async function readColumnTitles(page: Page, heading: string): Promise<string[]> {
  return page.evaluate((h) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const target = headings.find(el => el.textContent?.trim() === h);
    if (!target) return [];
    const column = target.closest('.column');
    if (!column) return [];
    const cards = Array.from(column.querySelectorAll('app-job-card .job-card__title')) as HTMLElement[];
    return cards.map(el => el.textContent?.trim() ?? '');
  }, heading);
}

test.describe('Lane sort toggle', () => {
  test('toggle button is visible and switches label between Custom and Date', async ({ page }) => {
    await clearLaneSortStorage(page);
    await page.goto('/');
    const toggle = page.getByTestId('lane-sort-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await expect(toggle).toContainText('Custom');
    await toggle.click();
    await expect(toggle).toContainText('Date');
    await toggle.click();
    await expect(toggle).toContainText('Custom');
  });

  test('Date sort orders cards in a lane by createdAt ascending; Custom restores backend order', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-lanesort-';
    await cleanup(PREFIX, watchPath);

    // Create three Ready jobs in known sequence so createdAt is known: A < B < C.
    const titleA = `${PREFIX}A-${Date.now()}`;
    const titleB = `${PREFIX}B-${Date.now() + 1}`;
    const titleC = `${PREFIX}C-${Date.now() + 2}`;
    const jobA = await createJob({ id: `${PREFIX}A`, title: titleA, watchPath, targetState: '2-ready' });
    await new Promise(r => setTimeout(r, 1100));
    const jobB = await createJob({ id: `${PREFIX}B`, title: titleB, watchPath, targetState: '2-ready' });
    await new Promise(r => setTimeout(r, 1100));
    const jobC = await createJob({ id: `${PREFIX}C`, title: titleC, watchPath, targetState: '2-ready' });

    // Reverse the custom order so Custom != createdAt: send the new order C,B,A
    // for these three jobs (other Ready jobs keep their relative order).
    const all = await listJobs();
    const readyOthers = all
      .filter(j => j.state === '2-ready' && ![jobA.id, jobB.id, jobC.id].includes(j.id))
      .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
    const customOrder = [
      ...readyOthers,
      { jobId: jobC.id, watchPath },
      { jobId: jobB.id, watchPath },
      { jobId: jobA.id, watchPath }
    ];
    await api('/api/jobs/reorder', { method: 'POST', body: JSON.stringify({ jobs: customOrder }) });

    try {
      await clearLaneSortStorage(page);
      await page.goto('/');
      await expect(page.getByTestId('lane-sort-toggle')).toBeVisible({ timeout: 10_000 });

      // Wait for our three test jobs to appear in the Ready column.
      await expect.poll(async () => {
        const titles = await readColumnTitles(page, 'Ready');
        return [titleA, titleB, titleC].every(t => titles.includes(t));
      }, { timeout: 10_000 }).toBeTruthy();

      // Custom mode: backend order C, B, A (filter out jobs we don't control).
      const customTitles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
      expect(customTitles).toEqual([titleC, titleB, titleA]);

      // Switch to Date mode and confirm A, B, C (oldest first).
      await page.getByTestId('lane-sort-toggle').click();
      await expect(page.getByTestId('lane-sort-toggle')).toContainText('Date');
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 5_000 }).toBe([titleA, titleB, titleC].join('|'));

      // Switch back to Custom and confirm C, B, A again.
      await page.getByTestId('lane-sort-toggle').click();
      await expect(page.getByTestId('lane-sort-toggle')).toContainText('Custom');
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 5_000 }).toBe([titleC, titleB, titleA].join('|'));
    } finally {
      await deleteJob(jobA.id, watchPath).catch(() => {});
      await deleteJob(jobB.id, watchPath).catch(() => {});
      await deleteJob(jobC.id, watchPath).catch(() => {});
    }
  });

  test('Date mode hides within-lane drop zones (drag-reorder disabled)', async ({ page }) => {
    await clearLaneSortStorage(page);
    await page.goto('/');
    await expect(page.getByTestId('lane-sort-toggle')).toBeVisible({ timeout: 10_000 });

    // In Custom mode, drop zones exist between cards.
    const dropZonesCustom = await page.locator('.column__drop-zone').count();
    expect(dropZonesCustom).toBeGreaterThan(0);

    await page.getByTestId('lane-sort-toggle').click();
    await expect(page.getByTestId('lane-sort-toggle')).toContainText('Date');

    const dropZonesDate = await page.locator('.column__drop-zone').count();
    expect(dropZonesDate).toBe(0);
  });
});
