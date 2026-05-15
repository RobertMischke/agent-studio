import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

/**
 * Regression coverage for "Wenn ich von einer Lane in die andere
 * verschiebe, dann merkt er sich nicht die Positionen, an die ich
 * getropft habe". Pre-fix the cross-lane drop emitted only a targetState
 * and the backend left the job's `order` untouched, so the card snapped
 * to whatever its stale source-lane order happened to sort to. The fix
 * routes the desired insertion slot through the `/move` endpoint and
 * rewrites the entire target lane to a dense 1..N sequence with the
 * dropped card pinned at the chosen slot.
 *
 * This spec drives the same DataTransfer dispatch the within-lane spec
 * uses, so it covers the same wire-up: drag a Ready card into a chosen
 * slot of Backlog, assert the card is at that slot in the rendered
 * column and that the backend persisted an order value that sorts the
 * card into the same position after a reload.
 */
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

async function dropZoneCount(page: Page, heading: string): Promise<number> {
  return page.evaluate((h) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const t = headings.find(el => el.textContent?.trim() === h);
    const col = t?.closest('.column');
    return col ? col.querySelectorAll('.column__drop-zone').length : 0;
  }, heading);
}

async function dispatchCrossLaneDrop(
  page: Page,
  sourceColumnHeading: string,
  sourceCardTitle: string,
  targetColumnHeading: string,
  /** 0 = before first card in target column, jobs.length = after last. */
  targetDropZoneIndex: number
): Promise<void> {
  await page.evaluate(({ sourceColumnHeading, sourceCardTitle, targetColumnHeading, targetDropZoneIndex }) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const sourceH = headings.find(el => el.textContent?.trim() === sourceColumnHeading);
    if (!sourceH) throw new Error(`Source column "${sourceColumnHeading}" not found`);
    const sourceCol = sourceH.closest('.column') as HTMLElement | null;
    if (!sourceCol) throw new Error('Source column root missing');

    const cards = Array.from(sourceCol.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.job-card__title')?.textContent?.trim() ?? '');
    const cardIndex = titles.indexOf(sourceCardTitle);
    if (cardIndex < 0) throw new Error(`Card "${sourceCardTitle}" not found in source column`);
    const card = cards[cardIndex];

    const targetH = headings.find(el => el.textContent?.trim() === targetColumnHeading);
    if (!targetH) throw new Error(`Target column "${targetColumnHeading}" not found`);
    const targetCol = targetH.closest('.column') as HTMLElement | null;
    if (!targetCol) throw new Error('Target column root missing');

    const dropZones = Array.from(targetCol.querySelectorAll('.column__drop-zone')) as HTMLElement[];
    const dropZone = dropZones[targetDropZoneIndex];
    if (!dropZone) throw new Error(`Drop zone ${targetDropZoneIndex} not found in target (have ${dropZones.length})`);

    const dataTransfer = new DataTransfer();
    card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
    card.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer }));
  }, { sourceColumnHeading, sourceCardTitle, targetColumnHeading, targetDropZoneIndex });
}

test.describe('Cross-lane drop preserves drop position', () => {
  test('drag from Ready into the middle of Backlog: dropped card stays at the chosen slot', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-xlane-';
    await cleanup(PREFIX, watchPath);

    // Three Backlog seeds + one Ready card that will be dragged across.
    // Order: b1, b2, b3 in Backlog; src in Ready.
    const t = Date.now();
    const titleB1 = `${PREFIX}B1-${t}`;
    const titleB2 = `${PREFIX}B2-${t + 1}`;
    const titleB3 = `${PREFIX}B3-${t + 2}`;
    const titleSrc = `${PREFIX}Src-${t + 3}`;
    const b1 = await createJob({ id: `${PREFIX}b1`, title: titleB1, watchPath, targetState: '0-backlog' });
    const b2 = await createJob({ id: `${PREFIX}b2`, title: titleB2, watchPath, targetState: '0-backlog' });
    const b3 = await createJob({ id: `${PREFIX}b3`, title: titleB3, watchPath, targetState: '0-backlog' });
    const src = await createJob({ id: `${PREFIX}src`, title: titleSrc, watchPath, targetState: '2-ready' });

    // Pin Backlog to [b1, b2, b3] regardless of leftover jobs in the lane.
    // Seed `src` with an order value that, without the fix, would make it
    // land at the BOTTOM of Backlog after the move (large order).
    const all = await listJobs();
    const backlogOthers = all
      .filter(j => j.state === '0-backlog' && ![b1.id, b2.id, b3.id].includes(j.id))
      .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
    await api('/api/jobs/reorder', {
      method: 'POST',
      body: JSON.stringify({
        jobs: [
          ...backlogOthers,
          { jobId: b1.id, watchPath },
          { jobId: b2.id, watchPath },
          { jobId: b3.id, watchPath }
        ]
      })
    });

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });

      // Confirm the trio is in Backlog before we drag.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2, titleB3].join('|'));

      // Source card is in Ready.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleSrc].join('|'));

      const backlogZones = await dropZoneCount(page, 'Backlog');
      expect(backlogZones).toBeGreaterThan(2);

      // Drag src onto drop-zone index 2 (between B2 and B3) — slot 2.
      // The dropped card must land at exactly that position, not at the
      // bottom and not snapped to some other slot.
      const movePost = page.waitForResponse(
        r => r.url().includes(`/api/jobs/${encodeURIComponent(src.id)}/move`) && r.request().method() === 'POST'
      );
      await dispatchCrossLaneDrop(page, 'Ready', titleSrc, 'Backlog', 2);
      const resp = await movePost;
      expect(resp.status()).toBe(200);

      // Optimistic + persisted Backlog order must be B1, B2, Src, B3.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2, titleSrc, titleB3].join('|'));

      // Reload to prove the position survives a fresh /api/jobs/grouped.
      await page.reload();
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2, titleSrc, titleB3].join('|'));

      // Persistence assertion at the backend layer: the moved card must
      // sort between b2 and b3 by its rewritten `order` field.
      const after = await listJobs();
      const ob1 = (after.find(j => j.id === b1.id) as any).order;
      const ob2 = (after.find(j => j.id === b2.id) as any).order;
      const osrc = (after.find(j => j.id === src.id) as any).order;
      const ob3 = (after.find(j => j.id === b3.id) as any).order;
      expect(ob1).toBeLessThan(ob2);
      expect(ob2).toBeLessThan(osrc);
      expect(osrc).toBeLessThan(ob3);
    } finally {
      await deleteJob(b1.id, watchPath).catch(() => {});
      await deleteJob(b2.id, watchPath).catch(() => {});
      await deleteJob(b3.id, watchPath).catch(() => {});
      await deleteJob(src.id, watchPath).catch(() => {});
    }
  });

  test('drag from Ready onto the top drop-zone of Backlog: dropped card lands at order 1', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-xlane-top-';
    await cleanup(PREFIX, watchPath);

    const t = Date.now();
    const titleB1 = `${PREFIX}B1-${t}`;
    const titleB2 = `${PREFIX}B2-${t + 1}`;
    const titleSrc = `${PREFIX}Src-${t + 2}`;
    const b1 = await createJob({ id: `${PREFIX}b1`, title: titleB1, watchPath, targetState: '0-backlog' });
    const b2 = await createJob({ id: `${PREFIX}b2`, title: titleB2, watchPath, targetState: '0-backlog' });
    const src = await createJob({ id: `${PREFIX}src`, title: titleSrc, watchPath, targetState: '2-ready' });

    const all = await listJobs();
    const backlogOthers = all
      .filter(j => j.state === '0-backlog' && ![b1.id, b2.id].includes(j.id))
      .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
    await api('/api/jobs/reorder', {
      method: 'POST',
      body: JSON.stringify({
        jobs: [
          ...backlogOthers,
          { jobId: b1.id, watchPath },
          { jobId: b2.id, watchPath }
        ]
      })
    });

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2].join('|'));

      // Drop on slot 0 (the top of Backlog).
      const movePost = page.waitForResponse(
        r => r.url().includes(`/api/jobs/${encodeURIComponent(src.id)}/move`) && r.request().method() === 'POST'
      );
      await dispatchCrossLaneDrop(page, 'Ready', titleSrc, 'Backlog', 0);
      await movePost;

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleSrc, titleB1, titleB2].join('|'));

      const after = await listJobs();
      const osrc = (after.find(j => j.id === src.id) as any).order;
      const ob1 = (after.find(j => j.id === b1.id) as any).order;
      const ob2 = (after.find(j => j.id === b2.id) as any).order;
      expect(osrc).toBeLessThan(ob1);
      expect(osrc).toBeLessThan(ob2);
    } finally {
      await deleteJob(b1.id, watchPath).catch(() => {});
      await deleteJob(b2.id, watchPath).catch(() => {});
      await deleteJob(src.id, watchPath).catch(() => {});
    }
  });
});
