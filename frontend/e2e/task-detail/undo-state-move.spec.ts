import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }
interface Job { id: string; state: string; order: number; watchPath: string; projectName: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID?.trim() || 'local-default' },
  });
}

function uid(suffix: string) {
  return `e2e-undo-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function listForProject(watchPath: string, projectName: string, state: string): Promise<Job[]> {
  const all = await api<Job[]>('/api/tasks?includeFixtures=true');
  return all.filter(j => j.state === state && j.watchPath === watchPath && j.projectName === projectName);
}

/**
 * Undo for state-changing actions triggered from the task-detail header.
 * The user's spec: every Complete / lane-move / lane-dropdown move from
 * the top-right detail header shows a non-blocking toast with a working
 * Undo that returns the card to its prior lane AND order slot.
 *
 * The Move/Undo toast docks in the BOTTOM-RIGHT notification pile so it
 * cannot cover the context menu that opens in the top-right corner; the
 * "Restored" confirmation (a plain success toast) lands in the default
 * top-right pile.
 *
 * Locks the load-bearing pieces of that contract.
 */
test.describe('Detail header — state-change undo toast', () => {
  test('lane dropdown move shows top-right toast with Undo that restores the prior lane and slot', async ({ page }) => {
    const wp = await getFirstWatchPath();

    // Plant three siblings in 2-ready so the target sits at slot 2 (0-based);
    // a successful undo must restore it to that slot, not append at the end.
    const siblingA = await createJob({ id: uid('a'), title: 'sib-a', watchPath: wp.path, targetState: '2-ready' });
    const siblingB = await createJob({ id: uid('b'), title: 'sib-b', watchPath: wp.path, targetState: '2-ready' });
    const target   = await createJob({ id: uid('t'), title: 'undo-target', watchPath: wp.path, targetState: '2-ready' });

    try {
      const projectName = (await getJob(target.id, wp.path)).projectName;
      const readyBefore = await listForProject(wp.path, projectName, '2-ready');
      const orderedBefore = [...readyBefore].sort((a, b) => a.order - b.order).map(j => j.id);
      const prevIndex = orderedBefore.indexOf(target.id);
      expect(prevIndex).toBeGreaterThanOrEqual(0);

      await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(wp.path)}`);
      const select = page.getByTestId('detail-state-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('2-ready');

      // Move from Ready -> Backlog via the lane dropdown.
      await select.selectOption('0-backlog');

      // Backend reflects the move.
      await expect.poll(async () => (await getJob(target.id, wp.path)).state, {
        timeout: 10_000,
      }).toBe('0-backlog');

      // Move/Undo toast appears in the BOTTOM-RIGHT pile with an Undo action.
      const bottomStack = page.getByTestId('notification-stack-bottom-right');
      await expect(bottomStack).toBeVisible({ timeout: 5_000 });
      const undoBtn = bottomStack.getByTestId('undo-action');
      await expect(undoBtn).toBeVisible({ timeout: 5_000 });
      await expect(undoBtn).toContainText('Undo');

      // Click Undo: the card must return to its original lane.
      await undoBtn.click();
      await expect.poll(async () => (await getJob(target.id, wp.path)).state, {
        timeout: 10_000,
      }).toBe('2-ready');

      // ...at the same slot.
      await expect.poll(async () => {
        const after = await listForProject(wp.path, projectName, '2-ready');
        const sorted = [...after].sort((a, b) => a.order - b.order).map(j => j.id);
        return sorted.indexOf(target.id);
      }, { timeout: 10_000 }).toBe(prevIndex);

      // Undo's own confirmation toast is a plain success toast in the
      // default top-right pile (not bottom-right, no second Undo).
      const topStack = page.getByTestId('notification-stack');
      await expect(topStack.getByTestId('notification-success').first()).toBeVisible({ timeout: 5_000 });
      // Critical anti-regression: clicking Undo MUST NOT spawn another
      // undo toast (would create an infinite ping-pong of toasts).
      await expect(page.getByTestId('undo-action')).toHaveCount(0);
    } finally {
      await deleteJob(target.id, wp.path).catch(() => {});
      await deleteJob(siblingA.id, wp.path).catch(() => {});
      await deleteJob(siblingB.id, wp.path).catch(() => {});
    }
  });

  test('undo toast is non-blocking — task-detail header remains interactive while it is visible', async ({ page }) => {
    // The user's explicit requirement: the toast must NOT get in the way
    // of the primary action area. We verify that by moving a task and
    // then immediately interacting with the dropdown that is anchored
    // next to where the toast renders. If the toast overlaid the
    // header's actions, the selectOption call would time out hovering
    // an obscured element.
    const wp = await getFirstWatchPath();
    const target = await createJob({ id: uid('nb'), title: 'undo-nonblock', watchPath: wp.path, targetState: '2-ready' });
    try {
      await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(wp.path)}`);
      const select = page.getByTestId('detail-state-select');
      await expect(select).toBeVisible({ timeout: 10_000 });

      await select.selectOption('0-backlog');
      await expect.poll(async () => (await getJob(target.id, wp.path)).state, {
        timeout: 10_000,
      }).toBe('0-backlog');

      // Toast is up (bottom-right pile).
      const bottomStack = page.getByTestId('notification-stack-bottom-right');
      await expect(bottomStack.getByTestId('undo-action')).toBeVisible({ timeout: 5_000 });

      // The header dropdown is still the visible+enabled element it was
      // before; the toast renders alongside it, not on top of it. We
      // bring it back ourselves (no Undo click) to a different lane and
      // assert the move succeeds without the toast intercepting events.
      await expect(select).toHaveValue('0-backlog', { timeout: 5_000 });
      await select.selectOption('1-preparation');
      await expect.poll(async () => (await getJob(target.id, wp.path)).state, {
        timeout: 10_000,
      }).toBe('1-preparation');
    } finally {
      await deleteJob(target.id, wp.path).catch(() => {});
    }
  });
});
