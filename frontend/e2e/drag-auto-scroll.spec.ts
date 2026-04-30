import { test, expect, Page } from '@playwright/test';
import path from 'node:path';
import { api, BACKEND } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

const RESULTS_DIR = 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/scrolling-with-drag-handle/results';
const PREFIX = 'e2e-dragscroll-';

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

async function cleanup(watchPath: string): Promise<void> {
  const stale = (await listJobs()).filter(j => j.watchPath === watchPath && j.id.startsWith(PREFIX));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

async function clearLaneSortStorage(page: Page): Promise<void> {
  await page.addInitScript(() => {
    try { localStorage.removeItem('laneSortMode'); } catch { /* ignore */ }
  });
}

test.describe('Drag auto-scroll', () => {
  test('dragging near the top edge scrolls the page up so other lanes become reachable', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanup(watchPath);

    // Create enough Ready cards to make the column extend below the viewport.
    const created: { id: string; title: string }[] = [];
    try {
      for (let i = 0; i < 30; i++) {
        const title = `${PREFIX}long-${i.toString().padStart(2, '0')}`;
        const job = await createJob({
          id: `${PREFIX}${i.toString().padStart(2, '0')}`,
          title,
          watchPath,
          targetState: '2-ready'
        });
        created.push({ id: job.id, title });
      }

      await clearLaneSortStorage(page);
      await page.setViewportSize({ width: 1400, height: 700 });
      await page.goto('/');
      await expect(page.getByTestId('lane-sort-toggle')).toBeVisible({ timeout: 10_000 });
      // Drag-reorder is only enabled in Custom mode.
      await expect(page.getByTestId('lane-sort-toggle')).toContainText('Custom');

      // Wait for the freshly created cards to appear.
      await expect.poll(async () => {
        return await page.evaluate((prefix) => {
          return document.querySelectorAll('app-job-card').length > 0
            && Array.from(document.querySelectorAll('.job-card__title'))
              .some(el => (el.textContent ?? '').startsWith(prefix));
        }, PREFIX);
      }, { timeout: 10_000 }).toBe(true);

      // Scroll the page down so a card from the bottom of the Ready column is
      // in view and the lane headers / the top of all columns are above the
      // viewport. Use the very bottom of the page.
      await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
      const scrollBefore = await page.evaluate(() => window.scrollY);
      expect(scrollBefore).toBeGreaterThan(50);

      // Pick a card that's currently in view to drag.
      const sourceCardTitle = created[created.length - 1].title;

      // Simulate the drag: dispatch dragstart on the card so the column
      // installs its document-level dragover listener, then dispatch dragover
      // on document with clientY=10 (well inside the 80px top edge zone).
      // Auto-scroll runs in requestAnimationFrame, so we wait a few frames.
      await page.evaluate((title) => {
        const cards = Array.from(document.querySelectorAll('app-job-card')) as HTMLElement[];
        const card = cards.find(c => c.querySelector('.job-card__title')?.textContent?.trim() === title);
        if (!card) throw new Error(`Card "${title}" not found`);
        const dt = new DataTransfer();
        card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt }));
        // Fire dragover at the very top of the viewport.
        document.dispatchEvent(new DragEvent('dragover', {
          bubbles: true, cancelable: true, dataTransfer: dt, clientX: 200, clientY: 10
        }));
      }, sourceCardTitle);

      // Wait long enough for several rAF ticks. Auto-scroll caps at 22px/frame
      // near the very edge, so 500ms should consume hundreds of px.
      await page.waitForTimeout(500);

      const scrollDuringTopEdge = await page.evaluate(() => window.scrollY);
      expect(scrollDuringTopEdge).toBeLessThan(scrollBefore - 100);

      // Releasing the drag should stop the auto-scroll loop.
      await page.evaluate(() => {
        document.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true }));
      });
      await page.waitForTimeout(150);
      const scrollAfterEnd = await page.evaluate(() => window.scrollY);
      await page.waitForTimeout(250);
      const scrollAfterIdle = await page.evaluate(() => window.scrollY);
      expect(scrollAfterIdle).toBe(scrollAfterEnd);

      // Snapshot showing the post-drag state for the job's results folder.
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'auto-scroll-after-drag.png'),
        fullPage: false
      });
    } finally {
      for (const c of created) {
        await deleteJob(c.id, watchPath).catch(() => {});
      }
    }
  });
});
