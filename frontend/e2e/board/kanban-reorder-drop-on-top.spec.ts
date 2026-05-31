/**
 * Acceptance for the recurring "kanban lane reorder drop-on-top must set
 * order=1" bug. The operator drags a card from the bottom of a lane and
 * releases it at the top. Expected: dragged card lands at the smallest
 * order in the lane. Pre-fix: drops above the first card's body (rather
 * than the narrow 14 px strip) bubbled to the column-level handler and
 * either did nothing or landed at strip i=1, leaving the dragged card at
 * order 2.
 *
 * This spec covers the three drop positions called out in the task:
 *   - drop above the first card's midpoint -> order 1 (smallest)
 *   - drop below the last card's midpoint  -> largest order
 *   - drop in the lower half of card N     -> sorts between N and N+1
 *
 * The synthetic DragEvents set `clientY` so the column-level
 * `computeDropSlotFromCursor` resolves deterministically. Native HTML5
 * drag is unreliable through Playwright's mouse APIs (dataTransfer
 * doesn't survive the synthetic mouse path), so we drive the same
 * DragEvent path the production code listens to — same pattern as
 * lane-reorder-drag.spec.ts and lane-reorder-drop-on-card.spec.ts.
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';

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
  const all = await api<{ id: string; watchPath: string }[]>('/api/jobs?includeFixtures=true');
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

interface DropOnCardArgs {
  page: Page;
  columnHeading: string;
  sourceCardTitle: string;
  /** Target card whose body receives the drop event. */
  targetCardTitle: string;
  /**
   * 0 = top edge of the target card, 1 = bottom edge. The column-level
   * handler converts cursor Y to a slot: < 0.5 = upper half (insert
   * before target), >= 0.5 = lower half (insert after).
   */
  cursorFraction: number;
}

async function dispatchDropOnCard({ page, columnHeading, sourceCardTitle, targetCardTitle, cursorFraction }: DropOnCardArgs): Promise<void> {
  await page.evaluate(({ columnHeading, sourceCardTitle, targetCardTitle, cursorFraction }) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const heading = headings.find(el => el.textContent?.trim() === columnHeading);
    if (!heading) throw new Error(`Column "${columnHeading}" not found`);
    const column = heading.closest('.column') as HTMLElement | null;
    if (!column) throw new Error(`Column root not found for "${columnHeading}"`);

    const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.job-card__title')?.textContent?.trim() ?? '');
    const sourceIdx = titles.indexOf(sourceCardTitle);
    const targetIdx = titles.indexOf(targetCardTitle);
    if (sourceIdx < 0) throw new Error(`Source "${sourceCardTitle}" not in "${columnHeading}"`);
    if (targetIdx < 0) throw new Error(`Target "${targetCardTitle}" not in "${columnHeading}"`);
    const sourceCard = cards[sourceIdx];
    const targetCard = cards[targetIdx];
    const rect = targetCard.getBoundingClientRect();
    const clientY = Math.round(rect.top + rect.height * cursorFraction);
    const clientX = Math.round(rect.left + rect.width / 2);

    const dt = new DataTransfer();
    sourceCard.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt, clientX, clientY }));
    targetCard.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt, clientX, clientY }));
    targetCard.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt, clientX, clientY }));
    sourceCard.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer: dt, clientX, clientY }));
  }, { columnHeading, sourceCardTitle, targetCardTitle, cursorFraction });
}

interface SeededJob { id: string; title: string; }

async function seedFiveCardsInReady(watchPath: string, prefix: string): Promise<SeededJob[]> {
  const now = Date.now();
  const titles = ['A', 'B', 'C', 'D', 'E'].map((c, i) => ({ id: `${prefix}${c}`, title: `${prefix}${c}-${now + i}` }));
  const created: SeededJob[] = [];
  for (const t of titles) {
    const job = await createJob({ id: t.id, title: t.title, watchPath, targetState: '2-ready', fixture: false });
    created.push({ id: job.id, title: t.title });
  }
  // Put our trio at the bottom of Ready in order A..E so the spec's drop
  // positions are deterministic regardless of what else lives in Ready.
  const all = await listJobs();
  const others = all
    .filter(j => j.state === '2-ready' && !created.some(c => c.id === j.id))
    .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
  await api('/api/jobs/reorder', {
    method: 'POST',
    body: JSON.stringify({
      jobs: [
        ...others,
        ...created.map(c => ({ jobId: c.id, watchPath }))
      ]
    })
  });
  return created;
}

test.describe('Kanban lane reorder: drop-on-top must set order=1', () => {
  test('drag bottom card to the top of the lane -> dragged card is at order 1', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-drop-on-top-';
    await cleanup(PREFIX, watchPath);

    const seeded = await seedFiveCardsInReady(watchPath, PREFIX);
    const [a, b, c, d, e] = seeded;

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([a.title, b.title, c.title, d.title, e.title].join('|'));

      // Drag the bottom card (E) onto the first card (A) with the cursor in
      // A's upper half. The dragged card must land at order 1.
      const reorderResp = page.waitForResponse(
        r => r.url().includes('/api/jobs/reorder') && r.request().method() === 'POST'
      );
      await dispatchDropOnCard({
        page,
        columnHeading: 'Ready',
        sourceCardTitle: e.title,
        targetCardTitle: a.title,
        cursorFraction: 0.25
      });
      await reorderResp;

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 5_000 }).toBe([e.title, a.title, b.title, c.title, d.title].join('|'));

      // Persisted order: E < A,B,C,D. Strictly smallest order in the lane.
      const after = await listJobs();
      const byTitle = new Map(after.map(j => [j.title, j]));
      const orderE = (byTitle.get(e.title) as any).order as number;
      const others = [a, b, c, d].map(j => (byTitle.get(j.title) as any).order as number);
      expect(typeof orderE).toBe('number');
      for (const o of others) expect(orderE).toBeLessThan(o);
    } finally {
      for (const j of seeded) await deleteJob(j.id, watchPath).catch(() => {});
    }
  });

  test('drag top card to the bottom of the lane -> dragged card has the largest order', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-drop-on-bottom-';
    await cleanup(PREFIX, watchPath);

    const seeded = await seedFiveCardsInReady(watchPath, PREFIX);
    const [a, b, c, d, e] = seeded;

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([a.title, b.title, c.title, d.title, e.title].join('|'));

      // Drag the top card (A) onto the last card (E) with the cursor in E's
      // lower half. The dragged card must land at the largest order.
      const reorderResp = page.waitForResponse(
        r => r.url().includes('/api/jobs/reorder') && r.request().method() === 'POST'
      );
      await dispatchDropOnCard({
        page,
        columnHeading: 'Ready',
        sourceCardTitle: a.title,
        targetCardTitle: e.title,
        cursorFraction: 0.75
      });
      await reorderResp;

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 5_000 }).toBe([b.title, c.title, d.title, e.title, a.title].join('|'));

      const after = await listJobs();
      const byTitle = new Map(after.map(j => [j.title, j]));
      const orderA = (byTitle.get(a.title) as any).order as number;
      const others = [b, c, d, e].map(j => (byTitle.get(j.title) as any).order as number);
      for (const o of others) expect(orderA).toBeGreaterThan(o);
    } finally {
      for (const j of seeded) await deleteJob(j.id, watchPath).catch(() => {});
    }
  });

  test('drop between two cards sorts the dragged card between the two neighbours', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-drop-between-';
    await cleanup(PREFIX, watchPath);

    const seeded = await seedFiveCardsInReady(watchPath, PREFIX);
    const [a, b, c, d, _e] = seeded;

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([a.title, b.title, c.title, d.title, seeded[4].title].join('|'));

      // Drag D onto A with cursor in A's lower half -> D should land between
      // A and B (slot 1).
      const reorderResp = page.waitForResponse(
        r => r.url().includes('/api/jobs/reorder') && r.request().method() === 'POST'
      );
      await dispatchDropOnCard({
        page,
        columnHeading: 'Ready',
        sourceCardTitle: d.title,
        targetCardTitle: a.title,
        cursorFraction: 0.75
      });
      await reorderResp;

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 5_000 }).toBe([a.title, d.title, b.title, c.title, seeded[4].title].join('|'));

      const after = await listJobs();
      const byTitle = new Map(after.map(j => [j.title, j]));
      const orderA = (byTitle.get(a.title) as any).order as number;
      const orderB = (byTitle.get(b.title) as any).order as number;
      const orderD = (byTitle.get(d.title) as any).order as number;
      expect(orderA).toBeLessThan(orderD);
      expect(orderD).toBeLessThan(orderB);
    } finally {
      for (const j of seeded) await deleteJob(j.id, watchPath).catch(() => {});
    }
  });
});
