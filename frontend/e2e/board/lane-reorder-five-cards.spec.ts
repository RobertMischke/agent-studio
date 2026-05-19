/**
 * Regression: dragging a card inside a lane must not drop it out of the
 * lane, and the new order must survive a page reload AND a polling tick.
 *
 * History — auto-review lane was the trigger. Earlier rounds shipped fixes
 * for "drop on a sibling card vanishes the card" (lane-reorder-drop-on-card)
 * and "drop-on-top must set order=1" (kanban-reorder-drop-on-top), both
 * against 3-5 cards in 2-ready. This spec pins the same contract directly
 * in 4-auto-review with the user's reported gesture (third card above the
 * first) at 5-card density, plus a polling-tick stability check that the
 * existing specs do not exercise.
 *
 * Per the acceptance for bug-auto-review-reorder-drops-card, the fix must
 * generalise: the same gesture works in 4-auto-review, 2-ready,
 * 5-human-review, and 0-backlog. Each lane gets its own test so a future
 * regression names the affected lane up front.
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs, moveJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID || 'local-default' }
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

interface LaneCase {
  state: string;
  heading: string;
  /** When non-null, jobs are created in `createSource` then moved to `state`. */
  createSource: string | null;
}

const LANES: LaneCase[] = [
  { state: '4-auto-review', heading: 'Auto Review',   createSource: '0-backlog' },
  { state: '2-ready',       heading: 'Human Ready',   createSource: null },
  { state: '5-human-review', heading: 'Human Review', createSource: '0-backlog' },
  { state: '0-backlog',     heading: 'Backlog',       createSource: null },
];

interface Seeded { id: string; title: string; }

async function seedFiveCardsIn(
  state: string,
  watchPath: string,
  prefix: string,
  createSource: string | null
): Promise<Seeded[]> {
  const createState = createSource ?? state;
  const now = Date.now();
  const seeds: Seeded[] = ['A', 'B', 'C', 'D', 'E'].map((c, i) => ({
    id: `${prefix}${c}`,
    title: `${prefix}${c}-${now + i}`,
  }));

  for (const s of seeds) {
    await createJob({
      id: s.id,
      title: s.title,
      watchPath,
      targetState: createState,
      fixture: false,
    });
  }
  if (createState !== state) {
    for (const s of seeds) {
      await moveJob(s.id, watchPath, state);
    }
  }
  // Anchor the seeded cards at the bottom of `state` in order A..E so the
  // drag indices are deterministic regardless of how many real cards live
  // in the lane.
  const all = await listJobs();
  const others = all
    .filter(j => j.state === state && !seeds.some(s => s.id === j.id))
    .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
  await api('/api/jobs/reorder', {
    method: 'POST',
    body: JSON.stringify({
      jobs: [...others, ...seeds.map(s => ({ jobId: s.id, watchPath }))],
    }),
  });
  return seeds;
}

interface DropOnCardArgs {
  page: Page;
  columnHeading: string;
  sourceCardTitle: string;
  targetCardTitle: string;
  /** 0 = top of target card, 1 = bottom. 0.25 lands in the upper half. */
  cursorFraction: number;
}

async function dispatchDropOnCard({
  page, columnHeading, sourceCardTitle, targetCardTitle, cursorFraction,
}: DropOnCardArgs): Promise<void> {
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

test.describe('Within-lane reorder at 5-card density', () => {
  for (const lane of LANES) {
    test(`drag third card above the first in ${lane.state} keeps it in the lane and persists across reload`, async ({ page }) => {
      const wp = await getFirstWatchPath();
      const watchPath = wp.path;
      const PREFIX = `e2e-five-${lane.state.replace(/[^a-z0-9]/gi, '')}-`;
      await cleanup(PREFIX, watchPath);

      const seeds = await seedFiveCardsIn(lane.state, watchPath, PREFIX, lane.createSource);
      const [a, b, c, d, e] = seeds;

      try {
        await page.goto('/');
        await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });

        // Confirm the seed trio is the tail of the lane in order A..E.
        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.join('|');
        }, { timeout: 10_000 }).toBe([a.title, b.title, c.title, d.title, e.title].join('|'));

        // The user's gesture: pick up the third card (C) and drop it above
        // the first (A). Pre-fix the card was removed from one signal
        // bucket but never added to another, so it vanished until reload.
        const reorderResp = page.waitForResponse(
          r => r.url().includes('/api/jobs/reorder') && r.request().method() === 'POST'
        );
        await dispatchDropOnCard({
          page,
          columnHeading: lane.heading,
          sourceCardTitle: c.title,
          targetCardTitle: a.title,
          cursorFraction: 0.25,
        });

        // Optimistic paint flips immediately: lane keeps all five cards,
        // C now leads.
        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.length === 5 ? titles.join('|') : `count=${titles.length}|${titles.join(',')}`;
        }, { timeout: 1500 }).toBe([c.title, a.title, b.title, d.title, e.title].join('|'));

        await reorderResp;

        // Wait at least one polling tick (live updates fire every 2 s) so
        // the silent /api/jobs/grouped poll runs after the suppression
        // window lifts. The order must hold.
        await page.waitForTimeout(2_500);
        const afterTick = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
        expect(afterTick).toEqual([c.title, a.title, b.title, d.title, e.title]);

        // Reload the page and confirm the persisted order survives a fresh
        // hydration from /api/jobs/grouped — this is the "never recovers
        // without reload" part of the bug report inverted into a check.
        await page.reload();
        await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.join('|');
        }, { timeout: 10_000 }).toBe([c.title, a.title, b.title, d.title, e.title].join('|'));
      } finally {
        for (const s of seeds) await deleteJob(s.id, watchPath).catch(() => {});
      }
    });
  }
});
