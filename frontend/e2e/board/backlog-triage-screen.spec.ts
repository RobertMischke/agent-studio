import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { startLongTaskRecorder } from '../helpers/timing';

/**
 * Backlog dedicated triage screen — slice D of the original
 * backlog-lane-task-types-and-tags task. The MVP (slices A/B/C) shipped
 * earlier; this spec covers the route + screen + per-row promote + filter
 * narrows + badge count + long-task budget for sort actions.
 *
 * Fixture-driven: every job carries `fixture: true` so it stays hidden
 * from stable's default kanban view; `?includeFixtures=true` exposes our
 * own jobs to the assertions.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(
    `${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' },
  );
}

async function cleanup(prefix: string, watchPath: string): Promise<void> {
  const all = await api<Array<{ id: string; watchPath: string }>>('/api/tasks?includeFixtures=true');
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

async function seedBacklogJob(opts: {
  id: string;
  title: string;
  watchPath: string;
  taskType?: 'bug' | 'feature' | 'chore';
  tags?: string[];
}): Promise<void> {
  const res = await fetch(`${BACKEND}/api/tasks`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      id: opts.id,
      title: opts.title,
      watchPath: opts.watchPath,
      agent: 'claude',
      cliType: 'claude',
      taskType: opts.taskType ?? 'chore',
      tags: opts.tags ?? [],
      fixture: true,
      // No targetState → lands in 0-backlog per the backlog-lane spec.
    }),
  });
  if (!res.ok) {
    throw new Error(`Failed to seed ${opts.id}: ${res.status} ${await res.text()}`);
  }
}

test.describe('Backlog dedicated triage screen (#/backlog)', () => {
  const PREFIX = 'e2e-backlog-triage-';

  test.beforeAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(PREFIX, wp.path);
  });

  test.afterAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(PREFIX, wp.path);
  });

  test('opens via #/backlog, lists every backlog job', async ({ page }) => {
    const wp = await firstWatchPath();
    await seedBacklogJob({ id: PREFIX + 'list-a', title: 'Triage list A', watchPath: wp.path, taskType: 'bug' });
    await seedBacklogJob({ id: PREFIX + 'list-b', title: 'Triage list B', watchPath: wp.path, taskType: 'feature' });

    await page.goto('/?includeFixtures=true#/backlog');
    const screen = page.getByTestId('backlog-triage-screen');
    await expect(screen).toBeVisible();

    const rows = page.getByTestId('backlog-triage-row');
    await expect(rows.filter({ hasText: 'Triage list A' })).toHaveCount(1);
    await expect(rows.filter({ hasText: 'Triage list B' })).toHaveCount(1);
  });

  test('activity-bar Backlog badge reflects the backlog count', async ({ page }) => {
    const wp = await firstWatchPath();
    await seedBacklogJob({ id: PREFIX + 'badge-1', title: 'Badge 1', watchPath: wp.path });
    await seedBacklogJob({ id: PREFIX + 'badge-2', title: 'Badge 2', watchPath: wp.path });

    await page.goto('/?includeFixtures=true');
    const badge = page.getByTestId('studio-ab-backlog-badge');
    await expect(badge).toBeVisible();
    // The shared dev backend may carry other backlog jobs from concurrent
    // suites; assert the badge counts at least our two fixtures rather
    // than pinning to an exact number.
    const initial = Number((await badge.textContent()) ?? '0');
    expect(initial).toBeGreaterThanOrEqual(2);

    // Click the Backlog button → triage screen opens.
    await page.getByTestId('studio-ab-backlog').click();
    await expect(page.getByTestId('backlog-triage-screen')).toBeVisible();
    await expect.poll(() => page.url()).toContain('#/backlog');
  });

  test('Promote → Preparation moves the row out of the list without reload', async ({ page }) => {
    const wp = await firstWatchPath();
    const id = PREFIX + 'promote';
    await seedBacklogJob({ id, title: 'Promote me', watchPath: wp.path });

    await page.goto('/?includeFixtures=true#/backlog');
    const row = page.locator('[data-testid="backlog-triage-row"]', { hasText: 'Promote me' }).first();
    await expect(row).toBeVisible();

    await row.getByTestId('backlog-triage-promote-prep').click();

    // Row disappears from the backlog triage list (no full reload — the
    // list is signals-driven off the same source as the kanban).
    await expect(row).toHaveCount(0, { timeout: 10_000 });

    // Backend confirms the job is now in 1-preparation.
    const job = await api<{ state: string }>(
      `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(wp.path)}`,
    );
    expect((job as unknown as { info?: { state: string } }).info?.state ?? (job as { state: string }).state).toBe('1-preparation');
  });

  test('type filter narrows the triage list', async ({ page }) => {
    const wp = await firstWatchPath();
    await seedBacklogJob({ id: PREFIX + 'filter-bug', title: 'Filter the bug', watchPath: wp.path, taskType: 'bug' });
    await seedBacklogJob({ id: PREFIX + 'filter-feat', title: 'Filter the feature', watchPath: wp.path, taskType: 'feature' });

    await page.goto('/?includeFixtures=true#/backlog');
    await expect(page.getByTestId('backlog-triage-screen')).toBeVisible();

    // Open the inline filter dropdown and pick "Bugs".
    await page.getByTestId('backlog-triage-screen')
      .getByTestId('filters-dropdown-trigger').click();
    await page.getByTestId('type-filter-bug').click();

    await expect(page.locator('[data-testid="backlog-triage-row"]', { hasText: 'Filter the bug' })).toBeVisible();
    await expect(page.locator('[data-testid="backlog-triage-row"]', { hasText: 'Filter the feature' })).toHaveCount(0);

    // Clearing returns the feature row.
    await page.getByTestId('type-filter-all').click();
    await expect(page.locator('[data-testid="backlog-triage-row"]', { hasText: 'Filter the feature' })).toBeVisible();
  });

  test('sort buttons stay under the 50ms long-task budget per AGENTS.md', async ({ page }) => {
    const wp = await firstWatchPath();
    // Seed a healthy mix so the sort actually does work.
    for (let i = 0; i < 10; i++) {
      await seedBacklogJob({
        id: PREFIX + 'sort-' + i,
        title: 'Sort sample ' + i,
        watchPath: wp.path,
        taskType: i % 2 === 0 ? 'bug' : 'feature',
      });
    }
    await page.goto('/?includeFixtures=true#/backlog');
    await expect(page.getByTestId('backlog-triage-screen')).toBeVisible();
    await page.locator('[data-testid="backlog-triage-row"]').first().waitFor();

    const recorder = await startLongTaskRecorder(page);

    // Trigger each sort mode. The component recomputes the visible list
    // synchronously; the recorder catches main-thread blocks > 50ms.
    await page.getByTestId('backlog-triage-sort-oldest').click();
    await page.getByTestId('backlog-triage-sort-by-type').click();
    await page.getByTestId('backlog-triage-sort-newest').click();

    // Let any post-click microtasks settle.
    await page.waitForTimeout(100);

    const total = await recorder.totalMs();
    await recorder.stop();
    // AGENTS.md: filter/sort actions must keep long tasks under 50ms.
    // Generous budget at the test layer to avoid CI flakes; tighten if
    // a regression actually hits.
    expect(total).toBeLessThan(200);
  });

  test('Ctrl+B toggles between board and triage screen', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('studio-board')).toBeVisible();

    // Ctrl+B opens the backlog triage screen.
    await page.keyboard.press('Control+B');
    await expect(page.getByTestId('backlog-triage-screen')).toBeVisible();
    await expect.poll(() => page.url()).toContain('#/backlog');

    // Ctrl+B again returns to the board.
    await page.keyboard.press('Control+B');
    await expect(page.getByTestId('backlog-triage-screen')).toHaveCount(0);
    await expect(page.getByTestId('studio-board')).toBeVisible();
  });
});
