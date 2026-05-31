/**
 * Acceptance for the "Gruppieren nach Epic" board view (Epics-als-Klammer
 * feature, board-grouping slice). The board tab carries a toggle next to the
 * compact toggle; flipping it on swaps the lane columns for a tree of epic
 * sections. Each epic section nests its sub-tasks (the tasks whose `epicId`
 * points at the epic) under a live "completed / total" rollup that mirrors the
 * backend `GET /api/epics` bucketing:
 *   completed  = 6-completed + 7-archive
 *   open       = 0-backlog   + 2-ready
 *   inProgress = total - completed - open
 *
 * The test seeds one epic plus four sub-tasks via the API, moves two to
 * 6-completed and one to 3-progress (leaving one in 2-ready), then asserts the
 * tree renders "2 / 4" with all four sub-tasks nested. Toggling back restores
 * the lane view.
 *
 * Routes are `/api/tasks*`; this spec inlines the task API calls it needs so it
 * does not depend on the still-`/api/jobs` shared helpers/jobs.ts.
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; title: string; state: string; watchPath: string; kind?: string; epicId?: string | null; }

const PREFIX = 'e2e-epic-group-';

/** Seed into the dedicated, near-empty "Playwright Test" project; fall back to the first path. */
async function getTestWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => p.name === 'Playwright Test') ?? paths[0];
}

async function listTasks(): Promise<TaskRow[]> {
  return api<TaskRow[]>('/api/tasks?includeFixtures=true');
}

async function createTask(input: {
  id: string;
  title: string;
  watchPath: string;
  kind?: string;
  epicId?: string;
  targetState?: string;
}): Promise<string> {
  const res = await api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id,
      title: input.title,
      watchPath: input.watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: null,
      promptMarkdown: null,
      targetState: input.targetState ?? '2-ready',
      kind: input.kind ?? 'task',
      epicId: input.epicId ?? null,
      fixture: false,
    }),
  });
  return res.id;
}

async function moveTask(jobId: string, watchPath: string, targetState: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(jobId)}/move?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'POST',
    body: JSON.stringify({ targetState }),
  });
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID || 'local-default' },
  });
}

/** Delete every task carrying this run's prefix across all watch paths. */
async function cleanup(): Promise<void> {
  const all = await listTasks();
  const stale = all.filter(j => j.id.startsWith(PREFIX));
  await Promise.all(stale.map(j => deleteTask(j.id, j.watchPath).catch(() => {})));
}

/** Titles rendered inside a specific epic group section, in DOM order. */
async function readGroupTitles(page: Page, epicId: string): Promise<string[]> {
  return page.evaluate((eid) => {
    const sec = document.querySelector(`[data-testid="epic-group-${eid}"]`);
    if (!sec) return [];
    const cards = Array.from(sec.querySelectorAll('app-job-card .task-card__title-text')) as HTMLElement[];
    return cards.map(el => el.textContent?.trim() ?? '');
  }, epicId);
}

test.describe('Board: group by epic', () => {
  // Seeding an epic + four sub-tasks + three moves, plus teardown, can run past
  // the default budget on a busy stable backend that rescans on every mutation.
  test.beforeEach(() => test.setTimeout(120_000));
  test.afterEach(() => cleanup());

  test('toggling "Group by epic" nests sub-tasks under their epic with a live rollup', async ({ page }, testInfo) => {
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    await cleanup();

    // One epic + four sub-tasks. Two land in 6-completed, one in 3-progress,
    // one stays in 2-ready -> rollup "2 / 4" (completed=2, inProgress=1, open=1).
    const epicId = await createTask({
      id: `${PREFIX}epic`,
      title: `${PREFIX}Checkout revamp`,
      watchPath,
      kind: 'epic',
      targetState: '2-ready',
    });

    const subIds: string[] = [];
    for (const n of ['1', '2', '3', '4']) {
      subIds.push(await createTask({
        id: `${PREFIX}sub-${n}`,
        title: `${PREFIX}sub ${n}`,
        watchPath,
        epicId,
        targetState: '2-ready',
      }));
    }
    await moveTask(subIds[0], watchPath, '6-completed');
    await moveTask(subIds[1], watchPath, '6-completed');
    await moveTask(subIds[2], watchPath, '3-progress');

    await page.goto('/');

    const toggle = page.locator('[data-testid="studio-board-epic-toggle"]');
    await expect(toggle).toBeVisible({ timeout: 15_000 });

    // Default view is lanes: the epic tree is not mounted yet.
    await expect(page.locator('[data-testid="epic-group-board"]')).toHaveCount(0);
    const lanesShot = testInfo.outputPath('board-lanes.png');
    await page.screenshot({ path: lanesShot, fullPage: false });
    await testInfo.attach('board-lanes', { path: lanesShot, contentType: 'image/png' });

    await toggle.click();

    const board = page.locator('[data-testid="epic-group-board"]');
    await expect(board).toBeVisible({ timeout: 10_000 });

    const group = page.locator(`[data-testid="epic-group-${epicId}"]`);
    await expect(group).toBeVisible({ timeout: 10_000 });

    // Rollup count settles to "2 / 4" once the grouped poll has the seeds.
    await expect(page.locator(`[data-testid="epic-group-count-${epicId}"]`))
      .toHaveText('2 / 4', { timeout: 15_000 });

    // All four sub-tasks render nested in the epic section (the epic card sits
    // first, so the section has 5 cards total; we assert the four sub-titles).
    await expect.poll(async () => {
      const titles = await readGroupTitles(page, epicId);
      return ['1', '2', '3', '4'].every(n => titles.includes(`${PREFIX}sub ${n}`));
    }, { timeout: 15_000 }).toBe(true);

    const treeShot = testInfo.outputPath('board-epic-tree.png');
    await page.screenshot({ path: treeShot, fullPage: false });
    await testInfo.attach('board-epic-tree', { path: treeShot, contentType: 'image/png' });

    // Toggling back restores the lane view and unmounts the tree.
    await toggle.click();
    await expect(board).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('[data-testid="studio-board"]')).toBeVisible({ timeout: 10_000 });
  });
});
