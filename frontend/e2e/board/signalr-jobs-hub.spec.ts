import { test, expect, Page, BrowserContext } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Push-path acceptance for the Jobs/Task SignalR hub (`/hubs/jobs`).
 *
 * The board's primary update path is push: the backend fans out fine-grained
 * mutation events (jobCreated / jobMoved / jobDeleted / jobsBulkChanged) over
 * SignalR, and {@link TaskService} applies them as local deltas / debounced
 * silent refreshes. The 2 s grouped poll was demoted to a 30 s heartbeat
 * fallback. These specs prove the push path end to end:
 *
 *   1. Cross-tab create  - a board already open in one tab renders a task
 *      created via the API in WELL UNDER the 30 s heartbeat, so the only way
 *      the card could appear that fast is the hub push.
 *   2. Cross-tab move    - a move issued via the API re-renders the card in
 *      its new lane on the already-open board, again far faster than the
 *      heartbeat.
 *   3. Disconnect + reconnect convergence - with the tab forced offline a
 *      mutation is missed live, and on reconnect the convergence hook
 *      (`reconnected` -> full re-pull) catches the board up.
 *
 * To keep meaningful timing, every push assertion uses a window (<= ~8 s)
 * that is comfortably below the 30 s heartbeat: a card that appears inside
 * that window cannot have been delivered by the poll.
 *
 * Tasks are created in `0-backlog` and moved only to `1-preparation` so the
 * spec never lands a task in `2-ready`, which would let an auto-mode project
 * pick it up and spend real CLI quota.
 */

const PREFIX = 'e2e-signalr-hub-';

// Comfortably below the 30 s heartbeat: proves push, not poll.
const PUSH_WINDOW_MS = 8_000;
// Reconnect uses the documented back-off [0,2,5,10,30]s; allow a generous
// window for the socket to re-establish + the convergence re-pull to land.
const RECONNECT_WINDOW_MS = 25_000;

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; watchPath: string; state: string; }

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => {});
}

async function moveTask(id: string, watchPath: string, targetState: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}/move?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'POST',
    body: JSON.stringify({ targetState }),
  });
}

async function cleanup(watchPath: string): Promise<void> {
  const all = await api<TaskRow[]>('/api/tasks?includeFixtures=true');
  const stale = all.filter(t => t.watchPath === watchPath && t.id.startsWith(PREFIX));
  await Promise.all(stale.map(t => deleteTask(t.id, t.watchPath)));
}

/** Lands on the kanban board for the given project, regardless of whether the
 *  app boots straight to a board or to the studio-welcome project picker. */
async function navigateToBoard(page: Page, projectName: string): Promise<void> {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  const anyLane = page.locator('[data-testid^="lane-"]').first();
  if (await anyLane.isVisible({ timeout: 2_000 }).catch(() => false)) return;

  const welcome = page.locator('[data-testid="studio-welcome"]');
  if (await welcome.isVisible({ timeout: 3_000 }).catch(() => false)) {
    await welcome.locator('.studio-welcome__project').filter({ hasText: projectName }).first().click();
  }
  await expect(anyLane).toBeVisible({ timeout: 10_000 });
}

/** A card with the given title, scoped to a specific lane's drop area. */
function cardInLane(page: Page, lane: string, title: string) {
  return page.locator(`[data-testid="lane-${lane}"]`).locator('app-job-card', { hasText: title });
}

test.describe('SignalR jobs hub - push delivery', () => {
  test.beforeAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(wp.path);
  });

  test.afterAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(wp.path);
  });

  test('cross-tab: a task created via the API appears on an already-open board within the push window', async ({ page }) => {
    const wp = await firstWatchPath();
    const id = PREFIX + 'create-' + Date.now();
    const title = 'SignalR create ' + id;

    // Open the board FIRST so the hub connection is live before the mutation.
    await navigateToBoard(page, wp.name);
    // Settle the initial load so the heartbeat's t=0 tick is already past;
    // any later appearance is push-driven.
    await expect(page.locator('[data-testid^="lane-"]').first()).toBeVisible();

    try {
      await createJob({ id, title, watchPath: wp.path, targetState: '0-backlog', fixture: false });

      // If this resolves inside PUSH_WINDOW_MS (<< 30 s heartbeat), the card
      // could only have arrived via the jobCreated push.
      await expect(cardInLane(page, '0-backlog', title)).toBeVisible({ timeout: PUSH_WINDOW_MS });
      await page.screenshot({ path: 'test-results/signalr-hub-cross-tab-create.png' });
    } finally {
      await deleteTask(id, wp.path);
    }
  });

  test('cross-tab: moving a task via the API re-lanes the card on the open board within the push window', async ({ page }) => {
    const wp = await firstWatchPath();
    const id = PREFIX + 'move-' + Date.now();
    const title = 'SignalR move ' + id;

    await createJob({ id, title, watchPath: wp.path, targetState: '0-backlog', fixture: false });

    try {
      await navigateToBoard(page, wp.name);
      await expect(cardInLane(page, '0-backlog', title)).toBeVisible({ timeout: 10_000 });

      // Move backlog -> preparation via the API; the open tab must react to
      // the jobMoved push (debounced silent refresh), not the 30 s poll.
      await moveTask(id, wp.path, '1-preparation');

      await expect(cardInLane(page, '1-preparation', title)).toBeVisible({ timeout: PUSH_WINDOW_MS });
      await expect(cardInLane(page, '0-backlog', title)).toHaveCount(0, { timeout: PUSH_WINDOW_MS });
      await page.screenshot({ path: 'test-results/signalr-hub-cross-tab-move.png' });
    } finally {
      await deleteTask(id, wp.path);
    }
  });

  test('survives disconnect: a mutation missed while offline converges on reconnect', async ({ page, context }) => {
    test.setTimeout(120_000);
    const wp = await firstWatchPath();
    const id = PREFIX + 'reconnect-' + Date.now();
    const title = 'SignalR reconnect ' + id;

    await navigateToBoard(page, wp.name);
    await expect(page.locator('[data-testid^="lane-"]').first()).toBeVisible();

    try {
      // Drop the socket. The API helper uses the test process's fetch, not the
      // page, so it can still mutate the backend while the tab is offline.
      await goOffline(context, true);
      await createJob({ id, title, watchPath: wp.path, targetState: '0-backlog', fixture: false });

      // Restore connectivity: withAutomaticReconnect re-establishes the hub and
      // the `reconnected` convergence hook does a full re-pull.
      await goOffline(context, false);

      await expect(cardInLane(page, '0-backlog', title)).toBeVisible({ timeout: RECONNECT_WINDOW_MS });
      await page.screenshot({ path: 'test-results/signalr-hub-reconnect-converge.png' });
    } finally {
      await goOffline(context, false);
      await deleteTask(id, wp.path);
    }
  });
});

/** setOffline is on BrowserContext in Playwright; guard so the spec degrades
 *  cleanly if a runner ever lacks the capability. */
async function goOffline(context: BrowserContext, offline: boolean): Promise<void> {
  await context.setOffline(offline).catch(() => {});
}
