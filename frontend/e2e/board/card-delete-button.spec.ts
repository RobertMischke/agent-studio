import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJobApi(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': 'local-default' },
  });
}

async function cleanup(prefix: string, watchPath: string): Promise<void> {
  const all = await api<Array<{ id: string; watchPath: string }>>('/api/tasks?includeFixtures=true');
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJobApi(j.id, j.watchPath).catch(() => {})));
}

async function navigateToBoard(page: Page, projectName: string): Promise<void> {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  const lane = page.locator('[data-testid^="lane-"]').first();
  const boardVisible = await lane.isVisible({ timeout: 2_000 }).catch(() => false);
  if (boardVisible) return;

  const welcome = page.locator('[data-testid="studio-welcome"]');
  if (await welcome.isVisible({ timeout: 2_000 }).catch(() => false)) {
    const projectBtn = welcome.locator('.studio-welcome__project')
      .filter({ hasText: projectName });
    await projectBtn.click();
  }
  await expect(lane).toBeVisible({ timeout: 10_000 });
}

test.describe('Card delete button', () => {
  const PREFIX = 'e2e-card-delete-';

  test.beforeAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(PREFIX, wp.path);
  });

  test.afterAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(PREFIX, wp.path);
  });

  test('clicking DELETE on a card opens the confirm dialog', async ({ page }) => {
    const wp = await firstWatchPath();
    const jobId = PREFIX + Date.now();
    await createJob({
      id: jobId,
      title: 'Delete test card',
      watchPath: wp.path,
      targetState: '2-ready',
      fixture: false,
    });

    try {
      await navigateToBoard(page, wp.name);

      const card = page.locator('app-job-card')
        .filter({ hasText: 'Delete test card' });
      await expect(card).toBeVisible({ timeout: 10_000 });

      // AGT-2020: Delete now lives in the card context menu (right-click / Menu
      // key), not on a hover trash button.
      await card.locator('[data-testid="task-card"]').click({ button: 'right' });
      const deleteItem = page.locator('[data-testid="card-ctx-item-delete-task"]');
      await expect(deleteItem).toBeVisible({ timeout: 3_000 });

      await deleteItem.click();

      const dialog = page.locator('[data-testid="confirm-dialog"]');
      await expect(dialog).toBeVisible({ timeout: 3_000 });
      await expect(page.locator('[data-testid="confirm-dialog-message"]'))
        .toContainText('removes the job folder');

      await page.screenshot({ path: 'test-results/card-delete-confirm-dialog.png' });

      await page.locator('[data-testid="confirm-dialog-cancel"]').click();
      await expect(dialog).not.toBeVisible({ timeout: 3_000 });
      await expect(card).toBeVisible({ timeout: 3_000 });
    } finally {
      await deleteJobApi(jobId, wp.path);
    }
  });

  test('confirming DELETE removes the card from the board', async ({ page }) => {
    const wp = await firstWatchPath();
    const jobId = PREFIX + 'confirm-' + Date.now();
    await createJob({
      id: jobId,
      title: 'Delete confirm test',
      watchPath: wp.path,
      targetState: '2-ready',
      fixture: false,
    });

    await navigateToBoard(page, wp.name);

    const card = page.locator('app-job-card')
      .filter({ hasText: 'Delete confirm test' });
    await expect(card).toBeVisible({ timeout: 10_000 });

    await card.locator('[data-testid="task-card"]').click({ button: 'right' });
    const deleteItem = page.locator('[data-testid="card-ctx-item-delete-task"]');
    await deleteItem.click();

    const dialog = page.locator('[data-testid="confirm-dialog"]');
    await expect(dialog).toBeVisible({ timeout: 3_000 });
    await page.locator('[data-testid="confirm-dialog-confirm"]').click();

    await expect(dialog).not.toBeVisible({ timeout: 3_000 });
    await expect(card).not.toBeVisible({ timeout: 10_000 });

    await page.screenshot({ path: 'test-results/card-delete-after-confirm.png' });
  });

  test('Delete via context menu works from a board card', async ({ page }) => {
    const wp = await firstWatchPath();
    const jobId = PREFIX + 'compact-' + Date.now();
    await createJob({
      id: jobId,
      title: 'Compact delete test',
      watchPath: wp.path,
      targetState: '2-ready',
      fixture: false,
    });

    try {
      await navigateToBoard(page, wp.name);

      // AGT-2035: card density was abolished; cards always render full.
      const card = page.locator('app-job-card')
        .filter({ hasText: 'Compact delete test' });
      await expect(card).toBeVisible({ timeout: 10_000 });

      await card.locator('[data-testid="task-card"]').click({ button: 'right' });
      const deleteItem = page.locator('[data-testid="card-ctx-item-delete-task"]');
      await expect(deleteItem).toBeVisible({ timeout: 3_000 });

      await page.screenshot({ path: 'test-results/card-delete-compact-menu.png' });

      await deleteItem.click();

      const dialog = page.locator('[data-testid="confirm-dialog"]');
      await expect(dialog).toBeVisible({ timeout: 3_000 });

      await page.locator('[data-testid="confirm-dialog-cancel"]').click();
      await expect(dialog).not.toBeVisible({ timeout: 3_000 });

      // Restore full mode
      const pressedAfter = await compactToggle.getAttribute('aria-pressed');
      if (pressedAfter === 'true') await compactToggle.click();
    } finally {
      await deleteJobApi(jobId, wp.path);
    }
  });
});
