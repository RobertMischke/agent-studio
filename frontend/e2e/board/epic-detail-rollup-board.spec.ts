/**
 * Acceptance for the epic detail rollup mini-board AND the in-place sub-task
 * navigation contract (ASS-1698).
 *
 * When the open card in the studio detail is an epic (kind=epic) it opens in its
 * own inline epic tab (`[data-testid="studio-epic"]`). The rollup pane below the
 * toolbar renders the epic's sub-tasks grouped into the lane/state columns they
 * currently sit in, spanning the full pane width.
 *
 * The navigation contract this locks: clicking a sub-task in the rollup swaps
 * THIS SAME lower detail panel from the epic to the task IN PLACE - it must not
 * open a second panel to the right. The epic-membership banner inside the
 * sub-task detail is the consistent "back to epic" path. The studio-epic main
 * therefore always holds exactly one `app-job-detail`.
 *
 * The test seeds one epic + four sub-tasks via the API and spreads them across
 * three lanes (two stay 2-ready, one -> 3-progress, one -> 6-completed), opens
 * the epic from the board, asserts the mini-board, then walks the in-place swap
 * and the return to the epic.
 *
 * Routes are `/api/tasks*`; this spec inlines the task API calls it needs so it
 * does not depend on the still-`/api/tasks` shared helpers/jobs.ts.
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; title: string; state: string; watchPath: string; kind?: string; epicId?: string | null; }

const PREFIX = 'e2e-epic-detail-board-';

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

async function cleanup(): Promise<void> {
  const all = await listTasks();
  const stale = all.filter(j => j.id.startsWith(PREFIX));
  await Promise.all(stale.map(j => deleteTask(j.id, j.watchPath).catch(() => {})));
}

/** Lane states in the mini-board, in DOM (left-to-right) order. */
async function readLaneOrder(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const lanes = Array.from(document.querySelectorAll('[data-testid="epic-rollup-board"] [data-testid="epic-rollup-lane"]')) as HTMLElement[];
    return lanes.map(el => el.getAttribute('data-lane') ?? '');
  });
}

test.describe('Epic detail: rollup board + in-place sub-task swap', () => {
  test.beforeEach(() => test.setTimeout(120_000));
  test.afterEach(() => cleanup());

  test('rollup board renders; sub-task click swaps the same panel in place (no right panel)', async ({ page }, testInfo) => {
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    await cleanup();

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
    // Spread across three lanes: sub-1 -> 6-completed, sub-2 -> 3-progress,
    // sub-3 + sub-4 stay 2-ready. Board should show Ready(2), In Progress(1),
    // Completed(1) in kanban order.
    await moveTask(subIds[0], watchPath, '6-completed');
    await moveTask(subIds[1], watchPath, '3-progress');

    await page.goto('/');
    await expect(page.locator('[data-testid="studio-board"]')).toBeVisible({ timeout: 20_000 });

    // Open the epic from the board: it lands in its own inline epic tab.
    await page.locator('app-job-card').filter({ hasText: `${PREFIX}Checkout revamp` }).first().click();

    const epicMain = page.locator('[data-testid="studio-epic"]');
    await expect(epicMain).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-testid="studio-epic-detail"]')).toBeVisible({ timeout: 15_000 });

    const pane = epicMain.locator('[data-testid="epic-rollup-pane"]');
    await expect(pane).toBeVisible({ timeout: 15_000 });
    await expect(pane.locator('[data-testid="epic-rollup-count"]'))
      .toHaveText('1 / 4 done', { timeout: 15_000 });

    const board = epicMain.locator('[data-testid="epic-rollup-board"]');
    await expect(board).toBeVisible({ timeout: 10_000 });

    // Exactly the three populated lanes, in kanban order. Empty lanes are hidden.
    await expect.poll(() => readLaneOrder(page), { timeout: 15_000 })
      .toEqual(['2-ready', '3-progress', '6-completed']);

    const readyLane = board.locator('[data-testid="epic-rollup-lane"][data-lane="2-ready"]');
    const progressLane = board.locator('[data-testid="epic-rollup-lane"][data-lane="3-progress"]');
    const completedLane = board.locator('[data-testid="epic-rollup-lane"][data-lane="6-completed"]');
    await expect(readyLane.locator('[data-testid="epic-rollup-lane-count"]')).toHaveText('2');
    await expect(progressLane.locator('[data-testid="epic-rollup-lane-count"]')).toHaveText('1');
    await expect(completedLane.locator('[data-testid="epic-rollup-lane-count"]')).toHaveText('1');

    const boardShot = testInfo.outputPath('epic-detail-lane-board.png');
    await page.screenshot({ path: boardShot, fullPage: false });
    await testInfo.attach('epic-detail-lane-board', { path: boardShot, contentType: 'image/png' });

    // The contract: exactly one detail panel is mounted while the epic is open.
    await expect(epicMain.locator('app-job-detail')).toHaveCount(1);

    // Clicking a sub-task swaps THIS panel from the epic to the task in place.
    // The epic detail (and its rollup board) is replaced - not pushed aside by a
    // second panel on the right.
    await board.locator(`[data-testid="epic-rollup-card"][data-sub-id="${subIds[1]}"]`).click();
    await expect(epicMain).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-testid="studio-epic-subtask-detail"]')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-testid="studio-epic-detail"]')).toHaveCount(0);
    await expect(epicMain.locator('[data-testid="epic-rollup-board"]')).toHaveCount(0);
    // Still a single detail panel - nothing opened to the right.
    await expect(epicMain.locator('app-job-detail')).toHaveCount(1);
    await expect(epicMain.locator('[data-testid="overview-title"]')).toContainText(`${PREFIX}sub 2`, { timeout: 15_000 });

    const swapShot = testInfo.outputPath('epic-detail-inplace-swap.png');
    await page.screenshot({ path: swapShot, fullPage: false });
    await testInfo.attach('epic-detail-inplace-swap', { path: swapShot, contentType: 'image/png' });

    // Back to the epic via the membership banner restores the rollup board in the
    // same single panel.
    await page.locator('[data-testid="epic-membership-banner"]').click();
    await expect(page.locator('[data-testid="studio-epic-detail"]')).toBeVisible({ timeout: 15_000 });
    await expect(epicMain.locator('[data-testid="epic-rollup-board"]')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-testid="studio-epic-subtask-detail"]')).toHaveCount(0);
    await expect(epicMain.locator('app-job-detail')).toHaveCount(1);
  });
});
