/**
 * Acceptance for the recurring "kanban lane reorder drop-on-top must set
 * order=1" bug. The operator drags a card from the bottom of a lane and
 * releases it at the top. Expected: dragged card lands at the smallest
 * order in the lane. Pre-fix, releasing above the first card's body (rather
 * than on the narrow per-card drop strip) bubbled to the column-level
 * handler, which for same-lane drops was a no-op or caught the wrong slot,
 * leaving the dragged card at order 2.
 *
 * Three drop positions, the ones called out in the task:
 *   - drop above the first card's midpoint -> order 1 (strictly smallest)
 *   - drop below the last card's midpoint  -> largest order
 *   - drop in the lower half of card N     -> sorts between N and N+1
 *
 * Selector / isolation policy (see playwright.config.ts and the sibling
 * lane-reorder-five-cards.spec.ts):
 *   - Locate the lane by the stable `data-testid="lane-<state>"` on the
 *     column root, not by heading text (lane labels are being renamed).
 *   - A card's title text lives in `.task-card__title-text`. The parent
 *     `.task-card__title` also holds the task-key chip, so its textContent
 *     is "RUN-816<title>" and never matches the seed prefix.
 *   - Seed into the dedicated "Playwright Test" project so real projects
 *     stay clean, and anchor the seeds at the HEAD of the lane so they all
 *     land in the initial virtual-scroll window. Assertions filter by the
 *     seed prefix, so merged cards from other projects are ignored.
 *   - The reorder `order` field is only the primary sort key under the
 *     `manual` lane-sort strategy; the per-lane defaults (2-ready =
 *     newest-first) use it only as a tiebreaker, so a drag would paint
 *     optimistically and snap back on the next hydration. We pin the lane
 *     under test to `manual` for the duration and restore the prior
 *     override afterwards.
 *
 * Native HTML5 drag is unreliable through Playwright's synthetic mouse
 * (DataTransfer doesn't survive that path), so we dispatch the same
 * DragEvent sequence the production code listens to, sharing one
 * DataTransfer across the gesture and setting `clientY` so the column's
 * `computeDropSlotFromCursor` resolves deterministically -- same pattern as
 * lane-reorder-drag.spec.ts and lane-reorder-five-cards.spec.ts.
 *
 * Routes are `/api/tasks*`; this spec inlines the few task API calls it
 * needs so it does not depend on the still-`/api/tasks` shared helpers/jobs.ts.
 */
import { test, expect, Page, TestInfo } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

const STATE = '2-ready';
/** The only sort strategy under which the reorder `order` field is authoritative. */
const MANUAL_SORT = 'manual';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; title: string; state: string; watchPath: string; order: number; }
interface LaneSortInfo { resolved: Record<string, string>; overrides: Record<string, string>; }

/** Seed into the dedicated, near-empty "Playwright Test" project; fall back to the first path. */
async function getTestWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => p.name === 'Playwright Test') ?? paths[0];
}

async function listTasks(): Promise<TaskRow[]> {
  return api<TaskRow[]>('/api/tasks?includeFixtures=true');
}

async function createTask(input: { id: string; title: string; watchPath: string }): Promise<void> {
  await api('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id,
      title: input.title,
      watchPath: input.watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: null,
      promptMarkdown: null,
      targetState: STATE,
      fixture: false,
    }),
  });
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID || 'local-default' },
  });
}

/**
 * Delete every task carrying this run's prefix, across ALL watch paths. The
 * board merges all projects into one lane, so a stray seed in any project
 * would inflate the prefix-filtered count. Sweeping everywhere keeps the
 * count deterministic.
 */
async function cleanup(prefix: string): Promise<void> {
  const all = await listTasks();
  const stale = all.filter(j => j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteTask(j.id, j.watchPath).catch(() => {})));
}

async function getLaneSortOverride(projectName: string, lane: string): Promise<string | null> {
  const info = await api<LaneSortInfo>(`/api/projects/${encodeURIComponent(projectName)}/lane-sort-strategies`);
  return info.overrides?.[lane] ?? null;
}

async function setLaneSortStrategy(projectName: string, lane: string, strategy: string): Promise<void> {
  await api(`/api/projects/${encodeURIComponent(projectName)}/lane-sort-strategy`, {
    method: 'PUT',
    body: JSON.stringify({ lane, strategy }),
  });
}

/** Lanes collapse to a rail (no cards in the DOM) and the state is sticky across reloads. */
async function ensureLaneExpanded(page: Page, state: string): Promise<void> {
  const lane = page.locator(`[data-testid="lane-${state}"]`);
  if (await lane.count() === 0) {
    const rail = page.locator(`[data-testid="lane-rail-${state}"]`);
    if (await rail.count() > 0) await rail.click();
  }
  await expect(lane).toBeVisible({ timeout: 10_000 });
}

async function readLaneTitles(page: Page, state: string): Promise<string[]> {
  return page.evaluate((st) => {
    const col = document.querySelector(`[data-testid="lane-${st}"]`);
    if (!col) return [];
    const cards = Array.from(col.querySelectorAll('app-job-card .task-card__title-text')) as HTMLElement[];
    return cards.map(el => el.textContent?.trim() ?? '');
  }, state);
}

async function readSeedTitles(page: Page, state: string, prefix: string): Promise<string[]> {
  return (await readLaneTitles(page, state)).filter(t => t.startsWith(prefix));
}

interface Seeded { id: string; title: string; }

/** Create five cards in 2-ready and anchor them, in A..E order, at the head of the lane. */
async function seedFiveCardsInReady(watchPath: string, prefix: string): Promise<Seeded[]> {
  const now = Date.now();
  const seeds: Seeded[] = ['A', 'B', 'C', 'D', 'E'].map((c, i) => ({
    id: `${prefix}${c}`,
    title: `${prefix}${c}-${now + i}`,
  }));
  for (const s of seeds) await createTask({ id: s.id, title: s.title, watchPath });

  const all = await listTasks();
  const others = all
    .filter(j => j.state === STATE && j.watchPath === watchPath && !seeds.some(s => s.id === j.id))
    .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
  await api('/api/tasks/reorder', {
    method: 'POST',
    body: JSON.stringify({ jobs: [...seeds.map(s => ({ jobId: s.id, watchPath })), ...others] }),
  });
  return seeds;
}

interface DropOnCardArgs {
  page: Page;
  state: string;
  sourceCardTitle: string;
  targetCardTitle: string;
  /** 0 = top edge of the target card, 1 = bottom. <0.5 = upper half (before), >=0.5 = lower half (after). */
  cursorFraction: number;
}

/**
 * Reproduce the operator's native HTML5 drag. The column root's `onDrop`
 * reads the dragged payload from the shared DataTransfer (set by dragstart)
 * and computes the insert slot from cursor-Y vs each card's midpoint. The
 * drop is dispatched on the target card (no per-card handler swallows it) so
 * it bubbles to the column root.
 */
async function dispatchDropOnCard({ page, state, sourceCardTitle, targetCardTitle, cursorFraction }: DropOnCardArgs): Promise<void> {
  await page.evaluate(({ state, sourceCardTitle, targetCardTitle, cursorFraction }) => {
    const col = document.querySelector(`[data-testid="lane-${state}"]`) as HTMLElement | null;
    if (!col) throw new Error(`Lane "${state}" not found`);
    const cards = Array.from(col.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.task-card__title-text')?.textContent?.trim() ?? '');
    const sourceIdx = titles.indexOf(sourceCardTitle);
    const targetIdx = titles.indexOf(targetCardTitle);
    if (sourceIdx < 0) throw new Error(`Source "${sourceCardTitle}" not rendered (have: ${titles.join(', ')})`);
    if (targetIdx < 0) throw new Error(`Target "${targetCardTitle}" not rendered (have: ${titles.join(', ')})`);
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
  }, { state, sourceCardTitle, targetCardTitle, cursorFraction });
}

/**
 * Snapshot the seed region of the Ready lane and attach it under the given
 * name so the JobArtifactReporter harvests it into the job results. The board
 * shows a merged "All projects" lane (thousands of px tall, ~160 cards) and
 * our seeds are NOT at the very top -- other projects' cards sort above them
 * -- so we scroll the first seed into view and clip a viewport-height window
 * starting there. That frames the reorder (the dragged card moving to the
 * head of the five seeds) instead of an unreadable full-lane sliver or the
 * wrong cards at the lane top.
 */
async function captureLane(page: Page, testInfo: TestInfo, name: string, prefix: string): Promise<void> {
  const lane = page.locator(`[data-testid="lane-${STATE}"]`);
  const firstSeed = lane
    .locator('app-job-card', { has: page.locator('.task-card__title-text', { hasText: prefix }) })
    .first();
  await firstSeed.scrollIntoViewIfNeeded();
  const file = testInfo.outputPath(`${name}.png`);
  const laneBox = await lane.boundingBox();
  const seedBox = await firstSeed.boundingBox();
  const viewport = page.viewportSize();
  if (laneBox && seedBox && viewport) {
    const top = Math.max(0, seedBox.y - 8);
    await page.screenshot({
      path: file,
      clip: { x: Math.max(0, laneBox.x), y: top, width: laneBox.width, height: Math.min(720, viewport.height - top) },
    });
  } else {
    await page.screenshot({ path: file });
  }
  await testInfo.attach(name, { path: file, contentType: 'image/png' });
}

test.describe('Kanban lane reorder: drop-on-top must set order=1', () => {
  // The stable backend rescans a large workspace and invalidates its index
  // cache on every mutation, so seed + reorder setup plus teardown can run
  // well past the default budget. Give each test room.
  test.beforeEach(() => test.setTimeout(180_000));

  test('drag bottom card to the top of the lane -> dragged card is at order 1', async ({ page }, testInfo) => {
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-drop-on-top-';
    await cleanup(PREFIX);
    const prior = await getLaneSortOverride(wp.name, STATE);
    await setLaneSortStrategy(wp.name, STATE, MANUAL_SORT);
    const seeds = await seedFiveCardsInReady(watchPath, PREFIX);
    const [a, b, c, d, e] = seeds;

    try {
      await page.goto('/');
      await ensureLaneExpanded(page, STATE);
      await expect.poll(async () => (await readSeedTitles(page, STATE, PREFIX)).join('|'), { timeout: 15_000 })
        .toBe([a.title, b.title, c.title, d.title, e.title].join('|'));

      await captureLane(page, testInfo, 'lane-reorder-before', PREFIX);

      // Drag the bottom card (E) onto the first card (A) with the cursor in
      // A's upper half. The dragged card must land at order 1.
      const reorderResp = page.waitForResponse(
        r => r.url().includes('/api/tasks/reorder') && r.request().method() === 'POST',
        { timeout: 15_000 }
      );
      await dispatchDropOnCard({ page, state: STATE, sourceCardTitle: e.title, targetCardTitle: a.title, cursorFraction: 0.25 });

      await expect.poll(async () => (await readSeedTitles(page, STATE, PREFIX)).join('|'), { timeout: 5_000 })
        .toBe([e.title, a.title, b.title, c.title, d.title].join('|'));
      await reorderResp;

      await captureLane(page, testInfo, 'lane-reorder-after', PREFIX);

      // Persisted order: E strictly smaller than A,B,C,D. Poll the read:
      // the backend task-index cache can briefly serve the pre-reorder
      // snapshot after the POST returns (the torn-read race documented in
      // lane-reorder-five-cards.spec.ts), so we retry until it settles.
      await expect.poll(async () => {
        const byTitle = new Map((await listTasks()).map(j => [j.title, j.order]));
        const oE = byTitle.get(e.title);
        if (oE == null) return false;
        return [a, b, c, d].every(j => { const o = byTitle.get(j.title); return o != null && oE < o; });
      }, { timeout: 10_000 }).toBe(true);
    } finally {
      for (const s of seeds) await deleteTask(s.id, watchPath).catch(() => {});
      await setLaneSortStrategy(wp.name, STATE, prior ?? '').catch(() => {});
    }
  });

  test('drag top card to the bottom of the lane -> dragged card has the largest order', async ({ page }) => {
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-drop-on-bottom-';
    await cleanup(PREFIX);
    const prior = await getLaneSortOverride(wp.name, STATE);
    await setLaneSortStrategy(wp.name, STATE, MANUAL_SORT);
    const seeds = await seedFiveCardsInReady(watchPath, PREFIX);
    const [a, b, c, d, e] = seeds;

    try {
      await page.goto('/');
      await ensureLaneExpanded(page, STATE);
      await expect.poll(async () => (await readSeedTitles(page, STATE, PREFIX)).join('|'), { timeout: 15_000 })
        .toBe([a.title, b.title, c.title, d.title, e.title].join('|'));

      // Drag the top card (A) onto the last card (E) with the cursor in E's
      // lower half. The dragged card must land at the largest order.
      const reorderResp = page.waitForResponse(
        r => r.url().includes('/api/tasks/reorder') && r.request().method() === 'POST',
        { timeout: 15_000 }
      );
      await dispatchDropOnCard({ page, state: STATE, sourceCardTitle: a.title, targetCardTitle: e.title, cursorFraction: 0.75 });

      await expect.poll(async () => (await readSeedTitles(page, STATE, PREFIX)).join('|'), { timeout: 5_000 })
        .toBe([b.title, c.title, d.title, e.title, a.title].join('|'));
      await reorderResp;

      // Persisted order: A strictly larger than B,C,D,E (poll past the cache race).
      await expect.poll(async () => {
        const byTitle = new Map((await listTasks()).map(j => [j.title, j.order]));
        const oA = byTitle.get(a.title);
        if (oA == null) return false;
        return [b, c, d, e].every(j => { const o = byTitle.get(j.title); return o != null && oA > o; });
      }, { timeout: 10_000 }).toBe(true);
    } finally {
      for (const s of seeds) await deleteTask(s.id, watchPath).catch(() => {});
      await setLaneSortStrategy(wp.name, STATE, prior ?? '').catch(() => {});
    }
  });

  test('drop in the lower half of a card sorts the dragged card between the two neighbours', async ({ page }) => {
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-drop-between-';
    await cleanup(PREFIX);
    const prior = await getLaneSortOverride(wp.name, STATE);
    await setLaneSortStrategy(wp.name, STATE, MANUAL_SORT);
    const seeds = await seedFiveCardsInReady(watchPath, PREFIX);
    const [a, b, c, d, e] = seeds;

    try {
      await page.goto('/');
      await ensureLaneExpanded(page, STATE);
      await expect.poll(async () => (await readSeedTitles(page, STATE, PREFIX)).join('|'), { timeout: 15_000 })
        .toBe([a.title, b.title, c.title, d.title, e.title].join('|'));

      // Drag D onto A with the cursor in A's lower half -> D lands between A and B (slot 1).
      const reorderResp = page.waitForResponse(
        r => r.url().includes('/api/tasks/reorder') && r.request().method() === 'POST',
        { timeout: 15_000 }
      );
      await dispatchDropOnCard({ page, state: STATE, sourceCardTitle: d.title, targetCardTitle: a.title, cursorFraction: 0.75 });

      await expect.poll(async () => (await readSeedTitles(page, STATE, PREFIX)).join('|'), { timeout: 5_000 })
        .toBe([a.title, d.title, b.title, c.title, e.title].join('|'));
      await reorderResp;

      // Persisted order: A < D < B (poll past the cache race). The optimistic
      // UI flips to A,D,B,C,E immediately, but the /api/tasks read can lag a
      // tick behind the post-reorder cache invalidation.
      await expect.poll(async () => {
        const byTitle = new Map((await listTasks()).map(j => [j.title, j.order]));
        const oA = byTitle.get(a.title);
        const oB = byTitle.get(b.title);
        const oD = byTitle.get(d.title);
        if (oA == null || oB == null || oD == null) return false;
        return oA < oD && oD < oB;
      }, { timeout: 10_000 }).toBe(true);
    } finally {
      for (const s of seeds) await deleteTask(s.id, watchPath).catch(() => {});
      await setLaneSortStrategy(wp.name, STATE, prior ?? '').catch(() => {});
    }
  });
});
