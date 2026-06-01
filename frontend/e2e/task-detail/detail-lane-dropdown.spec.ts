import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; }
interface TaskDetail { info: { id: string; state: string } }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

/**
 * Create a hidden fixture task via the canonical /api/tasks route. The
 * shared helpers/jobs.ts still targets the retired /api/jobs route (404),
 * so this spec talks to /api/tasks directly. `fixture: true` keeps the
 * task out of any auto-pick runner while it sits in 1-preparation.
 */
async function createTask(args: { id: string; title: string; watchPath: string; targetState: string }) {
  return api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: args.id,
      title: args.title,
      watchPath: args.watchPath,
      agent: 'claude',
      cliType: 'claude',
      targetState: args.targetState,
      fixture: true,
    }),
  });
}

async function getTask(id: string, watchPath: string): Promise<TaskDetail> {
  return api<TaskDetail>(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`);
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => { /* best-effort cleanup */ });
}

function uid() {
  return `e2e-lane-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * The studio detail view surfaces the current lane as a <select> in the
 * slim tab-bar header (`studio-lane-select`), so the user can move a task
 * to any lane straight from the detail view instead of dragging the card
 * on the board. The studio shell hides the projected <app-detail-header>
 * (and its `detail-state-select`); the tab-bar control is the one the user
 * actually sees. It is wired to POST /api/tasks/{id}/move; once the
 * response lands the parent re-fetches the detail so the dropdown reflects
 * the new lane.
 */
test.describe('Detail view — lane dropdown', () => {
  test('select changes the task lane on disk and in the UI', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `lane-dropdown ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('1-preparation');

      // Pick "Review" (5-human-review) — a non-adjacent lane to prove the
      // move is an arbitrary lane change, not a one-step "advance".
      await select.selectOption('5-human-review');

      // Backend reflects the new state on disk.
      await expect.poll(
        async () => (await getTask(created.id, wp.path)).info.state,
        { timeout: 10_000 },
      ).toBe('5-human-review');

      // Dropdown re-anchors once the parent re-fetches the detail.
      await expect(select).toHaveValue('5-human-review', { timeout: 10_000 });
      await expect(select).toBeEnabled();
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });
});
