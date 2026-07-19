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
 * existing specs do not exercise. The polling-tick check is the one that
 * caught the real defect: a torn read in the backend task-index cache
 * stomped the post-reorder Invalidate(), so the silent grouped poll served
 * the pre-reorder snapshot and the card reverted ~2 s after the drop
 * (fixed in TaskIndexCache via an _invalidationGen guard).
 *
 * Per the acceptance for bug-auto-review-reorder-drops-card, the fix must
 * generalise: the same gesture works in 4-auto-review, 2-ready,
 * 5-human-review, and 0-backlog. Each lane gets its own test so a future
 * regression names the affected lane up front.
 *
 * Selector / isolation policy (see playwright.config.ts):
 *   - Locate lanes by the stable `data-testid="lane-<state>"` on the column
 *     root, not by heading text — the lane LABELS are being renamed
 *     (Human Ready -> Ready, ...) but the state-keyed testid is stable.
 *   - A card's *title text* lives in `.task-card__title-text`. The parent
 *     `.task-card__title` (h3) also contains the task-key chip (e.g.
 *     "RUN-816"), so its textContent is "RUN-816<title>" — filtering on it
 *     never matches the seed prefix. Always read `.task-card__title-text`.
 *   - The board shows an "All projects" view that merges every watch path
 *     into one lane; the real 2-ready lane carries ~160 cards and activates
 *     CDK virtual scrolling. We seed into the dedicated "Playwright Test"
 *     project (keeps real projects clean) and anchor the five seeds at the
 *     HEAD of the lane via /api/tasks/reorder. The head anchor is what keeps
 *     every seed inside the initial virtual-scroll window even at 165-card
 *     density (verified: all five render in every lane). Assertions filter
 *     by the seed `prefix`, so merged cards from other projects are ignored.
 *   - Lane sort order is not assumed: it differs per lane (e.g. E,D,C,B,A in
 *     2-ready vs A,B,C,D,E in 5-human-review). We read the rendered order of
 *     the five seeds and drive the gesture off positions (3rd above 1st) so
 *     the test is robust to whatever default sort the lane applies.
 *
 * Routes are `/api/tasks*`; this spec inlines the few task API calls it needs
 * so it does not depend on the still-`/api/tasks` shared `helpers/jobs.ts`.
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; state: string; watchPath: string; projectName?: string; }

/**
 * Seed into the dedicated, near-empty "Playwright Test" project so the lanes
 * under test stay small (no virtualization) and isolated from real data.
 * Falls back to the first configured path if that project is absent.
 */
async function getTestWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => p.name === 'Playwright Test') ?? paths[0];
}

async function listTasks(): Promise<TaskRow[]> {
  return api<TaskRow[]>('/api/tasks');
}

async function createTask(input: {
  id: string; title: string; watchPath: string; targetState: string;
}): Promise<void> {
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
      targetState: input.targetState,
      fixture: false,
    }),
  });
}

async function moveTask(jobId: string, watchPath: string, targetState: string): Promise<void> {
  await api(
    `/api/tasks/${encodeURIComponent(jobId)}/move?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'POST', body: JSON.stringify({ targetState }) }
  );
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID || 'local-default' }
  });
}

/**
 * Delete every task carrying this run's prefix, across ALL watch paths. The
 * board merges all projects into one lane, so a leftover seed in any project
 * (e.g. a prior run that targeted a different watch path) would inflate the
 * prefix-filtered count and fail the "exactly five" poll. Sweeping by prefix
 * everywhere — not just the active watch path — keeps the count deterministic.
 */
async function cleanup(prefix: string): Promise<void> {
  const all = await api<TaskRow[]>('/api/tasks?includeFixtures=true');
  const stale = all.filter(j => j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteTask(j.id, j.watchPath).catch(() => {})));
}

interface LaneSortInfo { resolved: Record<string, string>; overrides: Record<string, string>; }

/** The project-settings override for a lane, or null when it uses the default. */
async function getLaneSortOverride(projectName: string, lane: string): Promise<string | null> {
  const info = await api<LaneSortInfo>(
    `/api/projects/${encodeURIComponent(projectName)}/lane-sort-strategies`
  );
  return info.overrides?.[lane] ?? null;
}

/** Set (or, with an empty string, clear) the lane's sort strategy override. */
async function setLaneSortStrategy(projectName: string, lane: string, strategy: string): Promise<void> {
  await api(`/api/projects/${encodeURIComponent(projectName)}/lane-sort-strategy`, {
    method: 'PUT',
    body: JSON.stringify({ lane, strategy }),
  });
}

/**
 * Lanes collapse to a rail (no cards in the DOM) and the collapse state is
 * sticky across reloads. Expand the target lane so its cards are queryable.
 */
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

interface LaneCase {
  state: string;
  /** When non-null, jobs are created in `createSource` then moved to `state`. */
  createSource: string | null;
}

const LANES: LaneCase[] = [
  { state: '4-auto-review', createSource: '0-backlog' },
  { state: '2-ready',       createSource: null },
  { state: '5-human-review', createSource: '0-backlog' },
  { state: '0-backlog',     createSource: null },
];

/** The sort strategy under which `order` (what reorder writes) is authoritative. */
const MANUAL_SORT = 'manual';

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
    await createTask({ id: s.id, title: s.title, watchPath, targetState: createState });
  }
  if (createState !== state) {
    for (const s of seeds) await moveTask(s.id, watchPath, state);
  }
  // Anchor the seeds at the HEAD of `state` (in A..E order) within this
  // watch path. Even scoped to one project the lane may carry a few
  // pre-existing cards; anchoring keeps the seeds grouped and in the
  // initial viewport. Assertions filter by `prefix`, so any non-seed cards
  // in the lane are ignored.
  const all = await listTasks();
  const others = all
    .filter(j => j.state === state && j.watchPath === watchPath && !seeds.some(s => s.id === j.id))
    .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
  await api('/api/tasks/reorder', {
    method: 'POST',
    body: JSON.stringify({
      jobs: [...seeds.map(s => ({ jobId: s.id, watchPath })), ...others],
    }),
  });
  return seeds;
}

interface DropOnCardArgs {
  page: Page;
  state: string;
  sourceCardTitle: string;
  targetCardTitle: string;
  /** 0 = top of target card, 1 = bottom. 0.25 lands above its midpoint. */
  cursorFraction: number;
}

/**
 * Reproduce the native HTML5 drag the user performs. `onDrop` on the column
 * root reads the dragged payload from the DataTransfer (set by `dragstart`)
 * and computes the insert slot from cursor-Y vs each card's midpoint, so a
 * single shared DataTransfer plus a drop in the target card's upper quarter
 * lands the source card immediately above the target. The drop is dispatched
 * on the card (no per-card drop handler) so it bubbles to the column root.
 */
async function dispatchDropOnCard({
  page, state, sourceCardTitle, targetCardTitle, cursorFraction,
}: DropOnCardArgs): Promise<void> {
  await page.evaluate(({ state, sourceCardTitle, targetCardTitle, cursorFraction }) => {
    const col = document.querySelector(`[data-testid="lane-${state}"]`) as HTMLElement | null;
    if (!col) throw new Error(`Lane "${state}" not found`);
    const cards = Array.from(col.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.task-card__title-text')?.textContent?.trim() ?? '');
    const sourceIdx = titles.indexOf(sourceCardTitle);
    const targetIdx = titles.indexOf(targetCardTitle);
    if (sourceIdx < 0) throw new Error(`Source "${sourceCardTitle}" not rendered in "${state}" (have: ${titles.join(', ')})`);
    if (targetIdx < 0) throw new Error(`Target "${targetCardTitle}" not rendered in "${state}" (have: ${titles.join(', ')})`);
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

/** Read the rendered order of just our five seeds (prefix-filtered). */
async function readSeedOrder(page: Page, state: string, prefix: string): Promise<string[]> {
  return (await readLaneTitles(page, state)).filter(t => t.startsWith(prefix));
}

test.describe('Within-lane reorder at 5-card density', () => {
  for (const lane of LANES) {
    test(`drag third card above the first in ${lane.state} keeps it in the lane and persists across reload`, async ({ page }) => {
      // The stable backend rescans a ~950-task workspace and invalidates its
      // index cache on every mutation, so the seed/move/reorder setup plus the
      // teardown deletes run well past Playwright's default budget. Give the
      // whole test room rather than racing the clock during setup.
      test.setTimeout(180_000);
      const wp = await getTestWatchPath();
      const watchPath = wp.path;
      const projectName = wp.name;
      const PREFIX = `e2e-five-${lane.state.replace(/[^a-z0-9]/gi, '')}-`;
      await cleanup(PREFIX);

      // Manual is the only sort strategy that treats the reorder `order` field
      // as the primary sort key, so it is the only one under which a within-lane
      // drag is a durable user signal that survives a reload. The per-lane
      // defaults (backlog/ready = newest-first, auto-review = last-activity,
      // human-review = oldest-first) derive position from key/timestamps and use
      // `order` only as a tiebreaker — under them a drag paints optimistically
      // and then snaps back on the next hydration no matter what, so they cannot
      // exercise the reorder-persistence contract the TaskIndexCache fix governs.
      // We pin the lane under test to Manual to assert that contract, and restore
      // the prior override afterwards. (Whether the default-sorted lanes should
      // also honor a manual drag, or disable dragging entirely, is the open
      // product question — surfaced in the task report, not decided here.)
      const priorOverride = await getLaneSortOverride(projectName, lane.state);
      await setLaneSortStrategy(projectName, lane.state, MANUAL_SORT);

      const seeds = await seedFiveCardsIn(lane.state, watchPath, PREFIX, lane.createSource);

      try {
        await page.goto('/');
        await ensureLaneExpanded(page, lane.state);

        // Wait until all five seeds are rendered in the lane, then capture
        // their actual rendered order (the lane's default sort is not
        // assumed). `initial[0]` is the first card, `initial[2]` the third.
        await expect.poll(async () => {
          return (await readSeedOrder(page, lane.state, PREFIX)).length;
        }, { timeout: 15_000 }).toBe(5);
        const initial = await readSeedOrder(page, lane.state, PREFIX);
        const [t0, t1, t2, t3, t4] = initial;
        const expectedAfter = [t2, t0, t1, t3, t4];

        // The user's gesture: pick up the third card and drop it above the
        // first. Pre-fix the card was removed from one signal bucket but
        // never added to another, so it vanished until reload.
        const reorderResp = page.waitForResponse(
          r => r.url().includes('/api/tasks/reorder') && r.request().method() === 'POST',
          { timeout: 15_000 }
        );
        await dispatchDropOnCard({
          page,
          state: lane.state,
          sourceCardTitle: t2,
          targetCardTitle: t0,
          cursorFraction: 0.25,
        });

        // Optimistic paint flips immediately: lane keeps all five cards,
        // the third now leads.
        await expect.poll(async () => {
          const titles = await readSeedOrder(page, lane.state, PREFIX);
          return titles.length === 5 ? titles.join('|') : `count=${titles.length}|${titles.join(',')}`;
        }, { timeout: 2_000 }).toBe(expectedAfter.join('|'));

        await reorderResp;

        // Wait at least one polling tick (live updates fire every 2 s) so the
        // silent /api/tasks/grouped poll runs after the suppression window
        // lifts. The order must hold — this is the assertion the torn-read
        // cache bug failed.
        await page.waitForTimeout(2_500);
        const afterTick = await readSeedOrder(page, lane.state, PREFIX);
        expect(afterTick).toEqual(expectedAfter);

        // Reload and confirm the persisted order survives a fresh hydration
        // from /api/tasks/grouped — the "never recovers without reload" part
        // of the report inverted into a check.
        await page.reload();
        await ensureLaneExpanded(page, lane.state);
        await expect.poll(async () => {
          return (await readSeedOrder(page, lane.state, PREFIX)).join('|');
        }, { timeout: 10_000 }).toBe(expectedAfter.join('|'));
      } finally {
        for (const s of seeds) await deleteTask(s.id, watchPath).catch(() => {});
        // Restore the lane's original sort: re-apply the prior override, or
        // clear it (empty string) so the lane falls back to its default.
        await setLaneSortStrategy(projectName, lane.state, priorOverride ?? '').catch(() => {});
      }
    });
  }
});
