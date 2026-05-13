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

test.describe('Drag auto-scroll', () => {
  test('dragging near the top edge of an overstocked lane scrolls that lane back up', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanup(watchPath);

    // Create enough Ready cards to make the column body scroll internally.
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

      await page.setViewportSize({ width: 1400, height: 700 });
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });

      // Wait for the freshly created cards to appear in the Ready lane.
      await expect.poll(async () => {
        return await page.evaluate((prefix) => {
          return document.querySelectorAll('app-job-card').length > 0
            && Array.from(document.querySelectorAll('.job-card__title'))
              .some(el => (el.textContent ?? '').startsWith(prefix));
        }, PREFIX);
      }, { timeout: 10_000 }).toBe(true);

      // Scroll the Ready column's body to the bottom so a card from the
      // bottom is in view and the top of the lane is above the body's
      // viewport. After ADR-0xxx the page itself no longer scrolls
      // vertically: each lane owns its own scroll container.
      const scrollBefore = await page.evaluate(() => {
        const body = document.querySelector('[data-testid="lane-2-ready"] .column__body') as HTMLElement | null;
        if (!body) throw new Error('Ready lane body not found');
        body.scrollTop = body.scrollHeight;
        return body.scrollTop;
      });
      expect(
        scrollBefore,
        'Ready lane body should be scrollable when 30 cards live in it',
      ).toBeGreaterThan(50);

      // Pick a card from the bottom of the Ready lane to drag.
      const sourceCardTitle = created[created.length - 1].title;

      // Simulate the drag: dispatch dragstart on the card so the column
      // installs its document-level dragover listener, then dispatch dragover
      // on document with clientY near the top edge of the Ready lane body
      // (well inside the 80 px edge zone). Auto-scroll runs in
      // requestAnimationFrame, so we wait a few frames.
      await page.evaluate((title) => {
        const cards = Array.from(document.querySelectorAll('app-job-card')) as HTMLElement[];
        const card = cards.find(c => c.querySelector('.job-card__title')?.textContent?.trim() === title);
        if (!card) throw new Error(`Card "${title}" not found`);
        const body = document.querySelector('[data-testid="lane-2-ready"] .column__body') as HTMLElement | null;
        if (!body) throw new Error('Ready lane body not found');
        const rect = body.getBoundingClientRect();
        const dt = new DataTransfer();
        card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt }));
        // Fire dragover ~10 px inside the lane body's top edge.
        document.dispatchEvent(new DragEvent('dragover', {
          bubbles: true, cancelable: true, dataTransfer: dt,
          clientX: rect.left + Math.floor(rect.width / 2),
          clientY: Math.floor(rect.top + 10),
        }));
      }, sourceCardTitle);

      // Wait long enough for several rAF ticks. Auto-scroll caps at 22px/frame
      // near the very edge, so 500ms should consume hundreds of px.
      await page.waitForTimeout(500);

      const scrollDuringTopEdge = await page.evaluate(() => {
        const body = document.querySelector('[data-testid="lane-2-ready"] .column__body') as HTMLElement | null;
        return body ? body.scrollTop : -1;
      });
      expect(
        scrollDuringTopEdge,
        `Ready lane body should have scrolled back toward the top during the drag-near-edge hold; ` +
        `was ${scrollBefore}px before, ${scrollDuringTopEdge}px after the dragover.`,
      ).toBeLessThan(scrollBefore - 100);

      // Releasing the drag should stop the auto-scroll loop.
      await page.evaluate(() => {
        document.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true }));
      });
      await page.waitForTimeout(150);
      const scrollAfterEnd = await page.evaluate(() => {
        const body = document.querySelector('[data-testid="lane-2-ready"] .column__body') as HTMLElement | null;
        return body ? body.scrollTop : -1;
      });
      await page.waitForTimeout(250);
      const scrollAfterIdle = await page.evaluate(() => {
        const body = document.querySelector('[data-testid="lane-2-ready"] .column__body') as HTMLElement | null;
        return body ? body.scrollTop : -1;
      });
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
