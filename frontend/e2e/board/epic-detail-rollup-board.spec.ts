/**
 * Acceptance for the epic detail rollup mini-board (ASS-593 follow-up:
 * "group sub-tasks by lane, full width"). When the open card in the task
 * detail is an epic (kind=epic), the rollup pane below the toolbar renders the
 * epic's sub-tasks grouped into the lane/state columns they currently sit in,
 * spanning the full pane width - replacing the old single narrow column list.
 *
 * The test seeds one epic + four sub-tasks via the API and spreads them across
 * three lanes (two stay 2-ready, one -> 3-progress, one -> 6-completed), opens
 * the epic from the board, and asserts the mini-board renders inside the
 * closable overlay without changing board context. Clicking a sub-task keeps
 * the epic master section mounted and swaps the nested detail below it.
 *
 * Routes are `/api/tasks*`; this spec inlines the task API calls it needs so it
 * does not depend on the still-`/api/jobs` shared helpers/jobs.ts.
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

test.describe('Epic detail: sub-tasks grouped by lane', () => {
  test.beforeEach(() => test.setTimeout(120_000));
  test.afterEach(() => cleanup());

  test('rollup pane renders a full-width lane board; cards open their detail', async ({ page }, testInfo) => {
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
    await expect.poll(() => new URL(page.url()).searchParams.get('job'), { timeout: 10_000 })
      .toBeNull();

    await page.locator('app-job-card').filter({ hasText: `${PREFIX}Checkout revamp` }).first().click();

    const overlay = page.locator('[data-testid="epic-overlay"]');
    await expect(overlay).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-testid="studio-board"]')).toBeVisible();
    await expect.poll(() => new URL(page.url()).searchParams.get('job'), { timeout: 10_000 })
      .toBeNull();

    const pane = overlay.locator('[data-testid="epic-rollup-pane"]');
    await expect(pane).toBeVisible({ timeout: 15_000 });
    await expect(pane.locator('[data-testid="epic-rollup-count"]'))
      .toHaveText('1 / 4 done', { timeout: 15_000 });

    const board = overlay.locator('[data-testid="epic-rollup-board"]');
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

    const chrome = await readyLane.evaluate((lane) => {
      const laneStyles = getComputedStyle(lane);
      const firstCard = lane.querySelector('[data-testid="epic-rollup-card"]');
      if (!(firstCard instanceof HTMLElement)) throw new Error('Missing rollup card');
      const firstCardStyles = getComputedStyle(firstCard);
      const firstRow = firstCard.closest('li');
      if (!(firstRow instanceof HTMLElement)) throw new Error('Missing rollup row');
      const firstRowStyles = getComputedStyle(firstRow);
      return {
        laneBackground: laneStyles.backgroundColor,
        laneBorderTopWidth: laneStyles.borderTopWidth,
        laneBorderLeftWidth: laneStyles.borderLeftWidth,
        cardBackground: firstCardStyles.backgroundColor,
        cardBorderTopWidth: firstCardStyles.borderTopWidth,
        rowSeparatorStyle: firstRowStyles.borderBottomStyle,
        rowSeparatorWidth: firstRowStyles.borderBottomWidth,
      };
    });
    expect(chrome).toMatchObject({
      laneBackground: 'rgba(0, 0, 0, 0)',
      laneBorderTopWidth: '0px',
      laneBorderLeftWidth: '0px',
      cardBackground: 'rgba(0, 0, 0, 0)',
      cardBorderTopWidth: '0px',
      rowSeparatorStyle: 'solid',
      rowSeparatorWidth: '1px',
    });

    const boardShot = testInfo.outputPath('epic-detail-lane-board.png');
    await page.screenshot({ path: boardShot, fullPage: false });
    await testInfo.attach('epic-detail-lane-board', { path: boardShot, contentType: 'image/png' });

    // Clicking a sub-task swaps the nested detail while the epic master stays
    // mounted, so the operator can keep navigating within the epic.
    await board.locator(`[data-testid="epic-rollup-card"][data-sub-id="${subIds[1]}"]`).click();
    await expect(overlay).toBeVisible({ timeout: 15_000 });
    await expect(pane).toBeVisible();
    await expect(board.locator(`[data-testid="epic-rollup-card"][data-sub-id="${subIds[1]}"]`))
      .toHaveAttribute('aria-current', 'true', { timeout: 10_000 });
    await expect(overlay.locator('[data-testid="epic-overlay-detail"]')).toBeVisible({ timeout: 15_000 });
    await expect(overlay.locator('[data-testid="overview-title"]')).toContainText(`${PREFIX}sub 2`, { timeout: 15_000 });
    await expect.poll(() => new URL(page.url()).searchParams.get('job'), { timeout: 10_000 })
      .toBe(subIds[1]);

    const secondDetailShot = testInfo.outputPath('epic-detail-persistent-master-detail.png');
    await page.screenshot({ path: secondDetailShot, fullPage: false });
    await testInfo.attach('epic-detail-persistent-master-detail', { path: secondDetailShot, contentType: 'image/png' });

    await board.locator(`[data-testid="epic-rollup-card"][data-sub-id="${subIds[2]}"]`).click();
    await expect(overlay).toBeVisible({ timeout: 15_000 });
    await expect(pane).toBeVisible();
    await expect(board.locator(`[data-testid="epic-rollup-card"][data-sub-id="${subIds[2]}"]`))
      .toHaveAttribute('aria-current', 'true', { timeout: 10_000 });
    await expect(overlay.locator('[data-testid="overview-title"]')).toContainText(`${PREFIX}sub 3`, { timeout: 15_000 });
    await expect.poll(() => new URL(page.url()).searchParams.get('job'), { timeout: 10_000 })
      .toBe(subIds[2]);

    await page.locator('[data-testid="epic-overlay-close"]').click();
    await expect(overlay).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('[data-testid="studio-board"]')).toBeVisible({ timeout: 10_000 });
    await expect.poll(() => new URL(page.url()).searchParams.get('job'), { timeout: 10_000 })
      .toBeNull();
  });
});
