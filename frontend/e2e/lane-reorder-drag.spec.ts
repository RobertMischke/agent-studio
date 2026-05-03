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

/**
 * Native HTML5 drag-and-drop is notoriously unreliable to drive through
 * Playwright's mouse APIs (the dataTransfer object doesn't survive the
 * synthetic mouse path). We instead dispatch the dragstart/dragover/drop
 * events directly via the DOM, which is exactly what the production code
 * listens for. This validates the wire-up: drop-zone handler -> jobReorder
 * emit -> /api/jobs/reorder POST -> job.json `order` field rewrite.
 */
async function dispatchDragReorder(
  page: Page,
  columnHeading: string,
  sourceCardTitle: string,
  /** Drop zone index — 0 means before first card, jobs.length means trailing zone. */
  targetDropZoneIndex: number
): Promise<void> {
  await page.evaluate(({ columnHeading, sourceCardTitle, targetDropZoneIndex }) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const heading = headings.find(el => el.textContent?.trim() === columnHeading);
    if (!heading) throw new Error(`Column "${columnHeading}" not found`);
    const column = heading.closest('.column') as HTMLElement | null;
    if (!column) throw new Error(`Column root not found for "${columnHeading}"`);

    const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.job-card__title')?.textContent?.trim() ?? '');
    const cardIndex = titles.indexOf(sourceCardTitle);
    if (cardIndex < 0) throw new Error(`Card "${sourceCardTitle}" not found in column`);
    const card = cards[cardIndex];

    const dropZones = Array.from(column.querySelectorAll('.column__drop-zone')) as HTMLElement[];
    const dropZone = dropZones[targetDropZoneIndex];
    if (!dropZone) throw new Error(`Drop zone ${targetDropZoneIndex} not found (have ${dropZones.length})`);

    const dataTransfer = new DataTransfer();
    card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
    card.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer }));
  }, { columnHeading, sourceCardTitle, targetDropZoneIndex });
}

test.describe('Lane drag-and-drop reorder', () => {
  test('dragging a card onto a later drop zone persists the new order', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-dragreorder-';
    await cleanup(PREFIX, watchPath);

    const titleA = `${PREFIX}A-${Date.now()}`;
    const titleB = `${PREFIX}B-${Date.now() + 1}`;
    const titleC = `${PREFIX}C-${Date.now() + 2}`;
    const jobA = await createJob({ id: `${PREFIX}A`, title: titleA, watchPath, targetState: '2-ready' });
    const jobB = await createJob({ id: `${PREFIX}B`, title: titleB, watchPath, targetState: '2-ready' });
    const jobC = await createJob({ id: `${PREFIX}C`, title: titleC, watchPath, targetState: '2-ready' });

    // Force the test trio to the *bottom* of Ready (other Ready jobs come first),
    // in order A, B, C. We then drag A so the resulting order is B, C, A.
    const all = await listJobs();
    const readyOthers = all
      .filter(j => j.state === '2-ready' && ![jobA.id, jobB.id, jobC.id].includes(j.id))
      .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
    await api('/api/jobs/reorder', {
      method: 'POST',
      body: JSON.stringify({
        jobs: [
          ...readyOthers,
          { jobId: jobA.id, watchPath },
          { jobId: jobB.id, watchPath },
          { jobId: jobC.id, watchPath }
        ]
      })
    });

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });

      // Wait for our three jobs to appear in Ready in order A, B, C.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleA, titleB, titleC].join('|'));

      // Find how many cards / drop zones are in Ready so we can target the
      // trailing drop zone (one past the last card).
      const dropZoneCount = await page.evaluate((heading) => {
        const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
        const h = headings.find(el => el.textContent?.trim() === heading);
        const col = h?.closest('.column');
        return col ? col.querySelectorAll('.column__drop-zone').length : 0;
      }, 'Ready');
      expect(dropZoneCount).toBeGreaterThan(0);

      // Drag A onto the trailing drop zone (last index) so A lands after C.
      await dispatchDragReorder(page, 'Ready', titleA, dropZoneCount - 1);

      // Frontend should optimistically reorder to B, C, A and persist via /api/jobs/reorder.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB, titleC, titleA].join('|'));

      // Verify persistence: re-fetch from backend and confirm the order field changed.
      const after = await listJobs();
      const trio = [titleA, titleB, titleC].map(t => after.find(j => j.title === t));
      expect(trio.every(j => !!j)).toBeTruthy();
      const orderA = (trio[0] as any).order ?? null;
      const orderB = (trio[1] as any).order ?? null;
      const orderC = (trio[2] as any).order ?? null;
      // After the drag, A's order should be greater than B's and C's.
      expect(orderA).toBeGreaterThan(orderB);
      expect(orderA).toBeGreaterThan(orderC);
    } finally {
      await deleteJob(jobA.id, watchPath).catch(() => {});
      await deleteJob(jobB.id, watchPath).catch(() => {});
      await deleteJob(jobC.id, watchPath).catch(() => {});
    }
  });
});
