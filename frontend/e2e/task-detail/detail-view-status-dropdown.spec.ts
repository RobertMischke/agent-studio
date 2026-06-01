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
 * task out of any auto-pick runner.
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
  return `e2e-status-dd-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * Pokes GET /api/tasks/{id} until it returns 200, then resolves. The dev
 * backend can return 404 on the first browser request for a freshly-created
 * fixture; a helper-side GET warms the lookup so the subsequent restoreFromUrl
 * call in the page does not race the indexer.
 */
async function waitForTaskIndexed(id: string, watchPath: string): Promise<void> {
  for (let i = 0; i < 20; i++) {
    try {
      await getTask(id, watchPath);
      return;
    } catch {
      await new Promise(r => setTimeout(r, 250));
    }
  }
  throw new Error(`Task ${id} never became visible to GET /api/tasks/{id}`);
}

/**
 * The studio detail view's lane control must be visibly discoverable as a
 * dropdown: a clear chevron and border on the lane chip, hover/focus
 * feedback, the full canonical lane catalogue in the menu, the same
 * transition the kanban board uses, and full keyboard reachability. The
 * control lives in the slim tab-bar header (`studio-lane-select`) because
 * the studio shell hides the projected <app-detail-header>. The companion
 * `detail-lane-dropdown` spec covers the move plumbing; this spec covers
 * the affordance contract.
 */
test.describe('Detail view — status dropdown (discoverability)', () => {
  test('control is visually distinct as a dropdown', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-visual ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });

      // The control has a visible border (not 0px) — the affordance the task
      // was about. We don't lock the exact pixel width; we just assert that
      // the border style is real so a future regression that strips it would
      // be caught.
      const style = await select.evaluate((el) => {
        const cs = getComputedStyle(el as HTMLSelectElement);
        return {
          borderTopWidth: cs.borderTopWidth,
          borderStyle: cs.borderTopStyle,
          cursor: cs.cursor,
          paddingRight: cs.paddingRight,
          appearance: cs.appearance ?? (cs as unknown as { webkitAppearance: string }).webkitAppearance ?? ''
        };
      });
      expect(parseFloat(style.borderTopWidth)).toBeGreaterThanOrEqual(1);
      expect(style.borderStyle).toBe('solid');
      expect(style.cursor).toBe('pointer');
      // Right-side padding leaves room for the chevron — bigger than the
      // default <select> would have, proving the chevron affordance is wired.
      expect(parseFloat(style.paddingRight)).toBeGreaterThanOrEqual(20);
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });

  test('dropdown lists every canonical lane', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-options ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });

      const values = await select.evaluate((el) =>
        Array.from((el as HTMLSelectElement).options).map(o => o.value)
      );

      // The lanes the dropdown is meant to expose. The header may include
      // additional lanes — the task list is the floor, not the ceiling.
      const required = [
        '1-preparation', '1a-orchestrator-prep', '2-ready', '3-progress',
        '4-auto-review', '5-human-review', '6-completed', '7-archive',
      ];
      for (const lane of required) {
        expect(values, `lane ${lane} should be in the dropdown`).toContain(lane);
      }
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });

  test('selecting a lane fires the same transition as the board', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-transition ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('1-preparation');

      // Watch for the exact backend move call the board issues.
      const movePromise = page.waitForRequest(req =>
        /\/api\/tasks\/[^/]+\/move/.test(req.url()) && req.method() === 'POST'
      );

      await select.selectOption('4-auto-review');

      const moveReq = await movePromise;
      const body = moveReq.postDataJSON() as { targetState?: string };
      expect(body.targetState).toBe('4-auto-review');

      await expect.poll(
        async () => (await getTask(created.id, wp.path)).info.state,
        { timeout: 10_000 },
      ).toBe('4-auto-review');

      // We deliberately do NOT assert the dropdown's post-move value here:
      // a cross-lane move can trigger the shell's triage auto-advance, which
      // hops to the next peer in the original lane and may close the panel
      // if no peer exists. The move contract is already proven by
      // `body.targetState` + the backend poll; post-move dropdown reflection
      // is covered by detail-lane-dropdown.spec.ts.
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });

  test('keyboard: focus, arrows change the lane', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-keyboard ${id}`,
      watchPath: wp.path,
      targetState: '2-ready',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('2-ready');

      // Direct focus via JS — Tab order in the tab-bar header passes through
      // many controls and is brittle to layout changes; what we actually need
      // to assert is that the control accepts keyboard focus at all, and
      // that arrow keys cycle the value.
      await select.focus();
      await expect(select).toBeFocused();

      // Native <select> + ArrowDown moves to the next option. From 2-ready
      // the next option in the header catalogue is 3-progress.
      await page.keyboard.press('ArrowDown');

      await expect.poll(
        async () => (await getTask(created.id, wp.path)).info.state,
        { timeout: 10_000 },
      ).toBe('3-progress');

      // See the transition test above: we do not re-read the dropdown value
      // here because the cross-lane move can trigger auto-advance and close
      // the detail panel, which is orthogonal to the keyboard contract.
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });
});
