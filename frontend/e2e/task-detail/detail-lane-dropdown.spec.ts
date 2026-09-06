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
 * shared helpers/jobs.ts still targets the retired /api/tasks route (404),
 * so this spec talks to /api/tasks directly. `fixture: true` keeps the
 * task out of any auto-pick runner while it sits in a manual lane.
 */
async function createTask(args: { id: string; title: string; watchPath: string; targetState: string; fixture?: boolean }) {
  return api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: args.id,
      title: args.title,
      watchPath: args.watchPath,
      agent: 'claude',
      cliType: 'claude',
      targetState: args.targetState,
      fixture: args.fixture ?? true,
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

function uid(suffix = '') {
  return `e2e-lane-${suffix}${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * The studio detail view surfaces a lane <select> in the slim tab-bar header
 * (`studio-lane-select`). It is a NAVIGATION control, not a move control:
 * picking a lane pages the detail view to a task that already lives in that
 * lane (the lane the pager iterates), it does NOT change the current task's
 * lane. Moving a task lives in the ⋯ context menu instead. The studio shell
 * hides the projected <app-detail-header>; the tab-bar control is the one the
 * user actually sees.
 */
test.describe('Detail view — lane dropdown (navigation)', () => {
  test('selecting a lane pages to a task in that lane without moving the current task', async ({ page }) => {
    const wp = await getFirstWatchPath();
    // The current task sits in 1-preparation; a sibling sits in 5-human-review
    // so the chosen lane is guaranteed to have at least one task to page to.
    // fixture:false so both render on the board / appear in the grouped signal
    // the navigation reads; neither lane is auto-picked by the runner, so the
    // fixtures stay put for the test.
    const current = await createTask({
      id: uid('cur-'),
      title: `lane-nav current`,
      watchPath: wp.path,
      targetState: '1-preparation',
      fixture: false,
    });
    const peer = await createTask({
      id: uid('peer-'),
      title: `lane-nav peer`,
      watchPath: wp.path,
      targetState: '5-human-review',
      fixture: false,
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(current.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('1-preparation');

      // Pick "Human review" (5-human-review), the lane the pager should now iterate.
      // Retry to absorb the grouped-signal first-poll race: until the
      // human-review peer lands in the grouped data, navigateToLane no-ops and
      // the native <select> reverts to the current lane.
      await expect(async () => {
        await select.selectOption('5-human-review');
        await expect(select).toHaveValue('5-human-review', { timeout: 2_000 });
      }).toPass({ timeout: 30_000 });
      // We paged AWAY from the 1-preparation task (it is not a human-review peer).
      await expect(page).not.toHaveURL(new RegExp(`job=${encodeURIComponent(current.id)}`), { timeout: 10_000 });

      // Crucially: navigation must NOT have moved the original task. It is
      // still sitting in 1-preparation on disk.
      await expect.poll(
        async () => (await getTask(current.id, wp.path)).info.state,
        { timeout: 10_000 },
      ).toBe('1-preparation');
    } finally {
      await deleteTask(current.id, wp.path);
      await deleteTask(peer.id, wp.path);
    }
  });

  test('orchestrator-controlled lanes are not offered as navigation targets', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const created = await createTask({
      id: uid('opts-'),
      title: `lane-nav options`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });

      const values = await select.evaluate((el) =>
        Array.from((el as HTMLSelectElement).options).map(o => o.value)
      );

      // In Progress / Auto Review are orchestrator-owned: never offered as a
      // manual navigation (or move) target.
      expect(values).not.toContain('3-progress');
      expect(values).not.toContain('4-auto-review');
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });
});
