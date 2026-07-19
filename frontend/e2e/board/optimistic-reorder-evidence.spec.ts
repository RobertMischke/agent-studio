/**
 * Evidence capture (run on demand). Drives the same scenario as
 * lane-reorder-drag.spec.ts and saves before/after screenshots showing
 * that the lane repaints optimistically while the reorder POST is still
 * stalled by 800 ms. Saved into the job folder's results/ so the review
 * pane has visual proof of the fix.
 *
 * Run via: npx playwright test e2e/optimistic-reorder-evidence.spec.ts
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';
import * as fs from 'fs';
import * as path from 'path';

const JOB_RESULTS = String.raw`C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\3-progress\das-sortieren-ist-buggy\results`;

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string) {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
}

async function readyColumnLocator(page: Page) {
  return page.locator('.column').filter({ has: page.locator('.column__title', { hasText: 'Ready' }) }).first();
}

async function dispatchDrag(page: Page, sourceTitle: string, dropZoneIndex: number) {
  await page.evaluate(({ sourceTitle, dropZoneIndex }) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const heading = headings.find(el => el.textContent?.trim() === 'Ready');
    const column = heading!.closest('.column') as HTMLElement;
    const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.job-card__title')?.textContent?.trim() ?? '');
    const card = cards[titles.indexOf(sourceTitle)];
    const zone = (Array.from(column.querySelectorAll('.column__drop-zone')) as HTMLElement[])[dropZoneIndex];
    const dt = new DataTransfer();
    card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt }));
    zone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
    zone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
    card.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer: dt }));
  }, { sourceTitle, dropZoneIndex });
}

test.skip(!process.env.CAPTURE_EVIDENCE, 'Set CAPTURE_EVIDENCE=1 to regenerate screenshots');
test('@evidence capture optimistic reorder before/after screenshots', async ({ page }) => {
  fs.mkdirSync(JOB_RESULTS, { recursive: true });
  const wp = await getFirstWatchPath();
  const watchPath = wp.path;
  const PREFIX = 'evidence-optim-';
  const all = await listJobs();
  await Promise.all(
    all.filter(j => j.watchPath === watchPath && j.id.startsWith(PREFIX))
       .map(j => deleteJob(j.id, j.watchPath).catch(() => {}))
  );

  const tA = `${PREFIX}A-${Date.now()}`;
  const tB = `${PREFIX}B-${Date.now() + 1}`;
  const tC = `${PREFIX}C-${Date.now() + 2}`;
  const a = await createJob({ id: `${PREFIX}A`, title: tA, watchPath, targetState: '2-ready' });
  const b = await createJob({ id: `${PREFIX}B`, title: tB, watchPath, targetState: '2-ready' });
  const c = await createJob({ id: `${PREFIX}C`, title: tC, watchPath, targetState: '2-ready' });

  const others = (await listJobs())
    .filter(j => j.state === '2-ready' && ![a.id, b.id, c.id].includes(j.id))
    .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
  await api('/api/tasks/reorder', {
    method: 'POST',
    body: JSON.stringify({ jobs: [...others, { jobId: a.id, watchPath }, { jobId: b.id, watchPath }, { jobId: c.id, watchPath }] })
  });

  try {
    // Stall the reorder POST so the screenshot timing window proves the
    // UI updates without waiting for the server.
    await page.route('**/api/tasks/reorder', async route => {
      await new Promise(r => setTimeout(r, 1500));
      try { await route.continue(); } catch { /* unrouted */ }
    });

    await page.goto('/');
    await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
    await expect.poll(async () => {
      const titles = await page.evaluate(() => {
        const h = (Array.from(document.querySelectorAll('.column__title')) as HTMLElement[])
          .find(el => el.textContent?.trim() === 'Ready');
        const col = h?.closest('.column');
        return col
          ? (Array.from(col.querySelectorAll('app-job-card .job-card__title')) as HTMLElement[])
              .map(el => el.textContent?.trim() ?? '')
              .filter(t => t.startsWith('evidence-optim-'))
          : [];
      });
      return titles.join('|');
    }, { timeout: 10_000 }).toBe([tA, tB, tC].join('|'));

    const ready = await readyColumnLocator(page);
    await ready.screenshot({ path: path.join(JOB_RESULTS, 'reorder-01-before-drag.png') });

    const zoneCount = await page.evaluate(() => {
      const h = (Array.from(document.querySelectorAll('.column__title')) as HTMLElement[])
        .find(el => el.textContent?.trim() === 'Ready');
      const col = h?.closest('.column');
      return col ? col.querySelectorAll('.column__drop-zone').length : 0;
    });

    // Drag A to the trailing drop zone. Capture immediately after dispatch
    // — the route handler still has ~1500 ms to go.
    const postStarted = page.waitForRequest(r => r.url().includes('/api/tasks/reorder') && r.method() === 'POST');
    await dispatchDrag(page, tA, zoneCount - 1);
    await postStarted;

    // Wait one animation frame, then screenshot — proves paint preceded POST.
    await page.evaluate(() => new Promise(r => requestAnimationFrame(() => r(null))));
    await ready.screenshot({ path: path.join(JOB_RESULTS, 'reorder-02-after-drop-post-still-pending.png') });

    // Wait for POST + grace, then the persisted state.
    await page.waitForResponse(r => r.url().includes('/api/tasks/reorder') && r.request().method() === 'POST');
    await page.unroute('**/api/tasks/reorder');
    await page.waitForTimeout(2000);
    await ready.screenshot({ path: path.join(JOB_RESULTS, 'reorder-03-after-server-confirm.png') });
  } finally {
    await deleteJob(a.id, watchPath).catch(() => {});
    await deleteJob(b.id, watchPath).catch(() => {});
    await deleteJob(c.id, watchPath).catch(() => {});
  }
});
