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
  test('drop updates the visible order optimistically, before the reorder POST returns', async ({ page }) => {
    // Regression for the "sorting is laggy" report. Previous behaviour:
    // onCardDrop emitted jobReorder, which awaited POST /api/jobs/reorder
    // and then a /api/jobs/grouped refresh round-trip before painting the
    // new order. Users observed the card snap back into place for several
    // hundred ms (longer when the backend was busy rewriting many job.json
    // files) and a second consecutive drag would be partially clobbered by
    // an in-flight silent poll. The contract this test pins: dropping a
    // card paints the new column order *before* the reorder POST resolves,
    // and a second drag dispatched against that optimistic state also
    // paints immediately.

    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-dragoptim-';
    await cleanup(PREFIX, watchPath);

    const titleA = `${PREFIX}A-${Date.now()}`;
    const titleB = `${PREFIX}B-${Date.now() + 1}`;
    const titleC = `${PREFIX}C-${Date.now() + 2}`;
    const jobA = await createJob({ id: `${PREFIX}A`, title: titleA, watchPath, targetState: '2-ready' });
    const jobB = await createJob({ id: `${PREFIX}B`, title: titleB, watchPath, targetState: '2-ready' });
    const jobC = await createJob({ id: `${PREFIX}C`, title: titleC, watchPath, targetState: '2-ready' });

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
      // Stall the reorder POST so we can prove the UI moves before the
      // server confirms. 800 ms is an order of magnitude beyond a
      // realistic wait and well above any animation/raf budget.
      const REORDER_DELAY_MS = 800;
      let resolvedReorders = 0;
      await page.route('**/api/jobs/reorder', async route => {
        await new Promise(r => setTimeout(r, REORDER_DELAY_MS));
        resolvedReorders++;
        await route.continue();
      });

      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleA, titleB, titleC].join('|'));

      const dropZoneCount = await page.evaluate((heading) => {
        const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
        const h = headings.find(el => el.textContent?.trim() === heading);
        const col = h?.closest('.column');
        return col ? col.querySelectorAll('.column__drop-zone').length : 0;
      }, 'Ready');
      expect(dropZoneCount).toBeGreaterThan(0);

      // Drag A to the trailing drop zone. The DOM reorder must happen
      // before the still-pending POST resolves, otherwise the user sees
      // the lag the bug report describes.
      const firstPostStarted = page.waitForRequest(
        r => r.url().includes('/api/jobs/reorder') && r.method() === 'POST'
      );
      const tStart = Date.now();
      await dispatchDragReorder(page, 'Ready', titleA, dropZoneCount - 1);

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 250 }).toBe([titleB, titleC, titleA].join('|'));

      const tFirstPaint = Date.now() - tStart;
      await firstPostStarted;
      expect(resolvedReorders, `expected first reorder POST still pending after optimistic paint (paint=${tFirstPaint}ms)`).toBe(0);

      // Wait for the first POST to resolve before triggering the second
      // drag. The optimistic layer is robust to overlapping POSTs at the
      // UI level, but the persistence assertion at the end of this test
      // requires deterministic POST ordering against the backend.
      await page.waitForResponse(
        r => r.url().includes('/api/jobs/reorder') && r.request().method() === 'POST'
      );

      // Second drag while the route handler still adds an 800 ms latency.
      // Same optimistic-paint contract: the new visible order appears
      // before the second POST resolves.
      const secondPostStarted = page.waitForRequest(
        r => r.url().includes('/api/jobs/reorder') && r.method() === 'POST'
      );
      const t2Start = Date.now();
      const resolvedAtT2 = resolvedReorders;
      await dispatchDragReorder(page, 'Ready', titleB, dropZoneCount - 1);

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 250 }).toBe([titleC, titleA, titleB].join('|'));

      const t2Paint = Date.now() - t2Start;
      await secondPostStarted;
      expect(resolvedReorders, `expected second reorder POST still pending after optimistic paint (paint=${t2Paint}ms)`).toBe(resolvedAtT2);

      // Wait for the second POST to resolve, then drop the route handler.
      await page.waitForResponse(
        r => r.url().includes('/api/jobs/reorder') && r.request().method() === 'POST'
      );
      await page.unroute('**/api/jobs/reorder');

      // Verify the persisted order on the backend matches what the user sees.
      await expect.poll(async () => {
        const after = await listJobs();
        const a = after.find(j => j.title === titleA);
        const b = after.find(j => j.title === titleB);
        const c = after.find(j => j.title === titleC);
        if (!a || !b || !c) return null;
        const oa = (a as any).order;
        const ob = (b as any).order;
        const oc = (c as any).order;
        return oc < oa && oa < ob ? 'ok' : `c=${oc},a=${oa},b=${ob}`;
      }, { timeout: 10_000 }).toBe('ok');
    } finally {
      await deleteJob(jobA.id, watchPath).catch(() => {});
      await deleteJob(jobB.id, watchPath).catch(() => {});
      await deleteJob(jobC.id, watchPath).catch(() => {});
    }
  });

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
      // Wait for the reorder POST to complete — the UI flips optimistically
      // (covered by the previous test) but persistence is what we assert here.
      const reorderResponse = page.waitForResponse(
        r => r.url().includes('/api/jobs/reorder') && r.request().method() === 'POST'
      );
      await dispatchDragReorder(page, 'Ready', titleA, dropZoneCount - 1);
      await reorderResponse;

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
