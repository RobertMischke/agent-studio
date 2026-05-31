import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

function uid() {
  return `e2e-lane-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * The detail-view header surfaces the current lane as a <select> the user
 * can change directly, instead of (or alongside) drag-and-drop on the board.
 * The control is wired to POST /api/jobs/{id}/move; once the response lands
 * the parent re-fetches the detail so the dropdown reflects the new lane.
 */
test.describe('Detail view — lane dropdown', () => {
  test('select changes the job lane on disk and in the UI', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createJob({
      id,
      title: `lane-dropdown ${id}`,
      watchPath: wp.path,
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('detail-state-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('2-ready');

      // Pick "Review" (5-human-review) — a non-adjacent lane to prove the
      // move is not a simple "advance one step" shortcut.
      await select.selectOption('5-human-review');

      // Backend reflects the new state.
      await expect.poll(
        async () => (await getJob(created.id, wp.path)).state,
        { timeout: 10_000 }
      ).toBe('5-human-review');

      // Dropdown reflects the new state once the parent re-fetches detail.
      await expect(select).toHaveValue('5-human-review', { timeout: 10_000 });
      await expect(select).toBeEnabled();
    } finally {
      await deleteJob(created.id, wp.path).catch(() => {});
    }
  });

  test('select disables itself while the move is in flight', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createJob({
      id,
      title: `lane-dropdown-pending ${id}`,
      watchPath: wp.path,
      targetState: '2-ready'
    });

    try {
      // Stall the move POST so we can observe the disabled state. The
      // backend would otherwise resolve fast enough that the disabled
      // flicker is invisible to a polling assertion.
      await page.route('**/api/jobs/*/move*', async route => {
        await new Promise(r => setTimeout(r, 800));
        await route.continue();
      });

      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('detail-state-select');
      await expect(select).toBeVisible({ timeout: 10_000 });

      await select.selectOption('1-preparation');

      // While the POST is stalled the control must be disabled.
      await expect(select).toBeDisabled({ timeout: 2_000 });

      // Once the request resolves and the detail re-loads, the control
      // becomes enabled again on the new lane.
      await expect(select).toBeEnabled({ timeout: 5_000 });
      await expect(select).toHaveValue('1-preparation', { timeout: 5_000 });
    } finally {
      await page.unroute('**/api/jobs/*/move*');
      await deleteJob(created.id, wp.path).catch(() => {});
    }
  });
});
