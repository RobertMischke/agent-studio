/**
 * Regression for the epic rollup gray box at tight vertical space / after a
 * resize. Bug: when the detail column is short and the lane board's content
 * exceeds the available height, the gray container (.epic-rollup) used to be
 * clipped by the detail column with nothing scrollable - so the lower lanes
 * (Auto Review, Human Review, Completed, Archive) spilled past the visible
 * bottom edge and were unreachable, making the gray box look "too small" and
 * the Archive lane appear to sit outside it.
 *
 * The fix makes the pane host (app-epic-rollup-pane) shrink to the available
 * height and scroll its own viewport, so the gray box keeps full content height
 * - fully enclosing every lane incl. Archive - while all lanes stay reachable.
 *
 * This seeds one epic + sub-tasks spread across every lane through Archive,
 * deep-links to the epic at a deliberately tight viewport, and asserts:
 *   1. the host is scrollable (overflow-y auto/scroll, scrollHeight > clientHeight);
 *   2. the gray box still encloses the Archive lane (archive bottom <= box bottom);
 *   3. scrolling to the bottom brings the Archive lane into the host viewport.
 *
 * Routes are `/api/tasks*`; the task API calls are inlined so this does not
 * depend on the still-`/api/tasks` shared helpers/jobs.ts.
 */
import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; title: string; state: string; watchPath: string; kind?: string; epicId?: string | null; }

const PREFIX = 'e2e-epic-rollup-tight-';

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

interface Geometry {
  hostOverflowY: string;
  hostScrollHeight: number;
  hostClientHeight: number;
  boxScrollHeight: number;
  archiveBelowBoxBy: number;
}

async function measure(page: Page): Promise<Geometry> {
  return page.evaluate(() => {
    const host = document.querySelector('app-epic-rollup-pane') as HTMLElement;
    const box = document.querySelector('.epic-rollup') as HTMLElement;
    const lanes = Array.from(document.querySelectorAll('[data-testid="epic-rollup-lane"]')) as HTMLElement[];
    const archive = lanes.find(l => l.getAttribute('data-lane') === '7-archive') ?? lanes[lanes.length - 1];
    return {
      hostOverflowY: getComputedStyle(host).overflowY,
      hostScrollHeight: host.scrollHeight,
      hostClientHeight: host.clientHeight,
      boxScrollHeight: box.scrollHeight,
      archiveBelowBoxBy: Math.round(archive.getBoundingClientRect().bottom - box.getBoundingClientRect().bottom),
    };
  });
}

test.describe('Epic rollup: tight viewport / resize keeps lanes enclosed and reachable', () => {
  test.beforeEach(() => test.setTimeout(180_000));
  test.afterEach(() => cleanup());

  test('gray box encloses every lane; host scrolls so Archive stays reachable', async ({ page }, testInfo) => {
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    await cleanup();

    const epicId = await createTask({
      id: `${PREFIX}epic`,
      title: `${PREFIX}Big epic`,
      watchPath,
      kind: 'epic',
      targetState: '2-ready',
    });

    // Spread two sub-tasks into each lane through Archive so the board is tall.
    const lanes = ['2-ready', '3-progress', '4-auto-review', '5-human-review', '6-completed', '7-archive'];
    let n = 0;
    for (const lane of lanes) {
      for (let k = 0; k < 2; k++) {
        const id = await createTask({ id: `${PREFIX}sub-${n}`, title: `${PREFIX}sub ${n} ${lane}`, watchPath, epicId, targetState: '2-ready' });
        if (lane !== '2-ready') await moveTask(id, watchPath, lane);
        n++;
      }
    }

    // Deliberately tight: narrow so lanes wrap and short so content > height.
    await page.setViewportSize({ width: 720, height: 560 });
    await page.goto(`/?job=${encodeURIComponent(epicId)}&watchPath=${encodeURIComponent(watchPath)}`);

    await expect(page.locator('[data-testid="studio-task"]')).toBeVisible({ timeout: 30_000 });
    const pane = page.locator('[data-testid="epic-rollup-pane"]');
    await expect(pane).toBeVisible({ timeout: 20_000 });
    const board = page.locator('[data-testid="epic-rollup-board"]');
    await expect(board).toBeVisible({ timeout: 15_000 });
    const archiveLane = board.locator('[data-testid="epic-rollup-lane"][data-lane="7-archive"]');
    await expect(archiveLane).toBeVisible({ timeout: 15_000 });

    const g = await measure(page);

    // 1. The host scrolls instead of overflowing the detail column unreachably.
    expect(['auto', 'scroll']).toContain(g.hostOverflowY);
    expect(g.hostScrollHeight).toBeGreaterThan(g.hostClientHeight);
    // The full gray box must be scrollable within the host (no lost content).
    expect(g.hostScrollHeight).toBeGreaterThanOrEqual(g.boxScrollHeight);

    // 2. The gray box still fully encloses the Archive lane (it does not spill out
    //    below the gray fill the way it did before the fix).
    expect(g.archiveBelowBoxBy).toBeLessThanOrEqual(0);

    // 3. Scrolling to the bottom brings Archive into the host's visible viewport.
    await archiveLane.scrollIntoViewIfNeeded();
    const archiveInView = await page.evaluate(() => {
      const host = document.querySelector('app-epic-rollup-pane') as HTMLElement;
      const lanes = Array.from(document.querySelectorAll('[data-testid="epic-rollup-lane"]')) as HTMLElement[];
      const archive = lanes.find(l => l.getAttribute('data-lane') === '7-archive') ?? lanes[lanes.length - 1];
      const h = host.getBoundingClientRect();
      const a = archive.getBoundingClientRect();
      return a.top >= Math.floor(h.top) && a.bottom <= Math.ceil(h.bottom);
    });
    expect(archiveInView).toBe(true);

    const shot = testInfo.outputPath('epic-rollup-tight-archive.png');
    await page.screenshot({ path: shot, fullPage: false });
    await testInfo.attach('epic-rollup-tight-archive', { path: shot, contentType: 'image/png' });
  });
});
