/**
 * Acceptance for the Epic overview, which renders inside a normal, closable
 * studio editor tab (no overlay, not pinned). The VS-Code studio shell carries
 * an activity-bar "Epics" button that is hidden when no epics exist; clicking it
 * opens (or focuses) an `epics` editor tab listing every epic from
 * `GET /api/epics` with a done / in-progress / open progress bar and an
 * expandable sub-task list. Clicking a sub-task navigates into that card's
 * detail.
 *
 * The Epics tab is just like any other tab: it carries a close-x, no pin glyph,
 * and is not sticky. The closable-tab contract is asserted alongside the rollup
 * so a future change cannot silently turn Epics back into a special overlay.
 *
 * The test seeds one epic + four sub-tasks via the API (two -> 6-completed,
 * one -> 3-progress, one stays 2-ready, so the rollup reads "2 / 4 done"),
 * opens the overview from the activity bar, expands the epic, asserts the four
 * sub-tasks render, then navigates into one and asserts the detail opens.
 *
 * Routes are `/api/tasks*`; this spec inlines the task API calls it needs so
 * it does not depend on the still-`/api/tasks` shared helpers/jobs.ts.
 */
import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; title: string; state: string; watchPath: string; kind?: string; epicId?: string | null; }

const PREFIX = 'e2e-epic-overview-';

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

test.describe('Epic overview screen', () => {
  // Seeding an epic + four sub-tasks + three moves, plus teardown, can run past
  // the default budget on a busy stable backend that rescans on every mutation.
  test.beforeEach(() => test.setTimeout(120_000));
  test.afterEach(() => cleanup());

  test('activity-bar Epics button opens a rollup; sub-tasks expand and navigate', async ({ page }, testInfo) => {
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
    await moveTask(subIds[0], watchPath, '6-completed');
    await moveTask(subIds[1], watchPath, '6-completed');
    await moveTask(subIds[2], watchPath, '3-progress');

    await page.goto('/');

    // The Epics button only mounts once the grouped poll sees the seeded epic.
    const epicsBtn = page.locator('[data-testid="studio-ab-epics"]');
    await expect(epicsBtn).toBeVisible({ timeout: 20_000 });

    await epicsBtn.click();

    const screen = page.locator('[data-testid="epic-overview-screen"]');
    await expect(screen).toBeVisible({ timeout: 10_000 });

    const card = page.locator(`[data-testid="epic-overview-card"][data-epic-id="${epicId}"]`);
    await expect(card).toBeVisible({ timeout: 10_000 });

    // Rollup count settles to "2 / 4 done" once the epic feed has the seeds.
    await expect(card.locator('[data-testid="epic-overview-card-count"]'))
      .toHaveText('2 / 4 done', { timeout: 15_000 });

    // Per-bucket stat chips reflect the bucketing (2 done, 1 in progress, 1 open).
    await expect(card.locator('[data-testid="epic-overview-stat-done"]')).toHaveText('2 done');
    await expect(card.locator('[data-testid="epic-overview-stat-prog"]')).toHaveText('1 in progress');
    await expect(card.locator('[data-testid="epic-overview-stat-open"]')).toHaveText('1 open');

    const overviewShot = testInfo.outputPath('epic-overview.png');
    await page.screenshot({ path: overviewShot, fullPage: false });
    await testInfo.attach('epic-overview', { path: overviewShot, contentType: 'image/png' });

    // Closable-tab contract: the Epics view is a normal editor tab, not an
    // overlay or a pinned tab. It must carry a close-x, no pin glyph, and not
    // be marked sticky. (Non-sticky tabs render `data-sticky` as null, so the
    // attribute is absent.)
    const epicsTab = page.locator('[role="tab"][data-tab-key="epics:__all__"]');
    await expect(epicsTab).toBeVisible({ timeout: 10_000 });
    await expect(epicsTab).not.toHaveAttribute('data-sticky', /.+/);
    await expect(epicsTab.locator('[data-testid="studio-tab-pin"]')).toHaveCount(0);
    await expect(epicsTab.getByRole('button', { name: 'Close tab' })).toHaveCount(1);

    // Expand the epic -> the four sub-tasks render as navigable rows.
    await card.locator('[data-testid="epic-overview-expand"]').click();
    const subs = card.locator('[data-testid="epic-overview-open-sub"]');
    await expect(subs).toHaveCount(4, { timeout: 10_000 });

    await card.scrollIntoViewIfNeeded();
    const expandedShot = testInfo.outputPath('epic-overview-expanded.png');
    await page.screenshot({ path: expandedShot, fullPage: false });
    await testInfo.attach('epic-overview-expanded', { path: expandedShot, contentType: 'image/png' });

    // Clicking a sub-task closes the overview and opens that card's detail.
    await card.locator(`[data-testid="epic-overview-open-sub"][data-sub-id="${subIds[0]}"]`).click();
    await expect(screen).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('[data-testid="studio-task"]')).toBeVisible({ timeout: 15_000 });
  });
});
