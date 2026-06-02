/**
 * REPRO scaffold for bug-auto-review-reorder-drops-card-2.
 *
 * The committed regression guard (lane-reorder-five-cards.spec.ts) pins each
 * lane under test to `manual`. In the "All projects" merged board view the
 * auto-review lane also carries cards from real projects that sit on the
 * DEFAULT `lane-entry` strategy, so a manual pin on the Playwright-Test
 * project makes the lane resolve to `mixed` -> drag-reorder is disabled and
 * no reorder ever fires. That guard therefore never exercised the real
 * default (`lane-entry`) the user is on when they report the card vanishing.
 *
 * This spec drives the user's exact gesture under the UNPINNED default
 * (`lane-entry`, override cleared) so the reproduction matches production.
 * Auto-review only — it is the reported lane and the fastest to iterate.
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; state: string; watchPath: string; projectName?: string; }

async function getTestWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => p.name === 'Playwright Test') ?? paths[0];
}

async function listTasks(): Promise<TaskRow[]> {
  return api<TaskRow[]>('/api/tasks');
}

async function createTask(input: { id: string; title: string; watchPath: string; targetState: string; }): Promise<void> {
  await api('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id, title: input.title, watchPath: input.watchPath,
      agent: 'claude', cliType: 'claude', model: null, promptMarkdown: null,
      targetState: input.targetState, fixture: false,
    }),
  });
}

async function moveTask(jobId: string, watchPath: string, targetState: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(jobId)}/move?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'POST', body: JSON.stringify({ targetState }) });
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE', headers: { 'x-client-id': process.env.PW_CLIENT_ID || 'local-default' } });
}

async function cleanup(prefix: string): Promise<void> {
  const all = await api<TaskRow[]>('/api/tasks?includeFixtures=true');
  const stale = all.filter(j => j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteTask(j.id, j.watchPath).catch(() => {})));
}

interface LaneSortInfo { resolved: Record<string, string>; overrides: Record<string, string>; }
async function getLaneSortOverride(projectName: string, lane: string): Promise<string | null> {
  const info = await api<LaneSortInfo>(`/api/projects/${encodeURIComponent(projectName)}/lane-sort-strategies`);
  return info.overrides?.[lane] ?? null;
}
async function setLaneSortStrategy(projectName: string, lane: string, strategy: string): Promise<void> {
  await api(`/api/projects/${encodeURIComponent(projectName)}/lane-sort-strategy`,
    { method: 'PUT', body: JSON.stringify({ lane, strategy }) });
}

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
async function readSeedOrder(page: Page, state: string, prefix: string): Promise<string[]> {
  return (await readLaneTitles(page, state)).filter(t => t.startsWith(prefix));
}

interface Seeded { id: string; title: string; }
async function seedFiveCardsIn(state: string, watchPath: string, prefix: string, createSource: string | null): Promise<Seeded[]> {
  const createState = createSource ?? state;
  const now = Date.now();
  const seeds: Seeded[] = ['A', 'B', 'C', 'D', 'E'].map((c, i) => ({ id: `${prefix}${c}`, title: `${prefix}${c}-${now + i}` }));
  for (const s of seeds) await createTask({ id: s.id, title: s.title, watchPath, targetState: createState });
  if (createState !== state) for (const s of seeds) await moveTask(s.id, watchPath, state);
  const all = await listTasks();
  const others = all
    .filter(j => j.state === state && j.watchPath === watchPath && !seeds.some(s => s.id === j.id))
    .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
  await api('/api/tasks/reorder', {
    method: 'POST',
    body: JSON.stringify({ jobs: [...seeds.map(s => ({ jobId: s.id, watchPath })), ...others] }),
  });
  return seeds;
}

interface DropOnCardArgs { page: Page; state: string; sourceCardTitle: string; targetCardTitle: string; cursorFraction: number; }
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

test.describe('REPRO default-sort within-lane reorder', () => {
  const STATE = '4-auto-review';
  test(`drag third above first in ${STATE} under DEFAULT lane-entry`, async ({ page }) => {
    test.setTimeout(180_000);
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    const projectName = wp.name;
    const PREFIX = `e2e-repro-${STATE.replace(/[^a-z0-9]/gi, '')}-`;
    await cleanup(PREFIX);

    // DEFAULT path: clear any override so the lane resolves to lane-entry,
    // matching what real projects (and the user) are on.
    const priorOverride = await getLaneSortOverride(projectName, STATE);
    await setLaneSortStrategy(projectName, STATE, '');

    const seeds = await seedFiveCardsIn(STATE, watchPath, PREFIX, '0-backlog');

    try {
      await page.goto('/');
      await ensureLaneExpanded(page, STATE);

      await expect.poll(async () => (await readSeedOrder(page, STATE, PREFIX)).length, { timeout: 15_000 }).toBe(5);
      const initial = await readSeedOrder(page, STATE, PREFIX);
      console.log('REPRO initial seed order:', JSON.stringify(initial));
      const [t0, t1, t2, t3, t4] = initial;
      const expectedAfter = [t2, t0, t1, t3, t4];

      // Reliable: count the reorder POST *requests* (response is slow on stable).
      let reorderRequests = 0;
      page.on('request', (req) => {
        if (req.method() === 'POST' && req.url().includes('/api/tasks/reorder')) reorderRequests++;
      });

      await dispatchDropOnCard({ page, state: STATE, sourceCardTitle: t2, targetCardTitle: t0, cursorFraction: 0.25 });

      await page.waitForTimeout(1_500);
      const afterDrop = await readSeedOrder(page, STATE, PREFIX);
      console.log('REPRO reorder POST requests fired:', reorderRequests);
      console.log('REPRO optimistic order :', JSON.stringify(afterDrop));
      console.log('REPRO expected order   :', JSON.stringify(expectedAfter));

      await page.waitForTimeout(3_500);
      const afterTick = await readSeedOrder(page, STATE, PREFIX);
      console.log('REPRO after-tick order :', JSON.stringify(afterTick));

      // What did the backend actually persist? (prefix-filtered, in lane order)
      const persisted = (await api<TaskRow[]>(`/api/tasks/grouped`).then((g: any) => g.autoReview ?? []))
        .filter((j: any) => (j.title ?? '').startsWith(PREFIX))
        .map((j: any) => j.title);
      console.log('REPRO backend persisted:', JSON.stringify(persisted));

      await page.reload();
      await ensureLaneExpanded(page, STATE);
      let afterReload: string[] = [];
      await expect.poll(async () => {
        afterReload = await readSeedOrder(page, STATE, PREFIX);
        return afterReload.length;
      }, { timeout: 15_000 }).toBe(5);
      console.log('REPRO after-reload     :', JSON.stringify(afterReload));

      // Assertions: card must stay (count 5), optimistic flip, persistence.
      expect(reorderRequests, 'no reorder POST fired').toBeGreaterThan(0);
      expect(afterDrop.length, 'card vanished from optimistic view').toBe(5);
      expect(afterDrop, 'optimistic order wrong').toEqual(expectedAfter);
      expect(afterTick.length, 'card vanished after polling tick').toBe(5);
      expect(afterTick, 'snapped back / lost order after tick').toEqual(expectedAfter);
      expect(afterReload, 'order not persisted across reload').toEqual(expectedAfter);
    } finally {
      for (const s of seeds) await deleteTask(s.id, watchPath).catch(() => {});
      await setLaneSortStrategy(projectName, STATE, priorOverride ?? '').catch(() => {});
    }
  });
});
