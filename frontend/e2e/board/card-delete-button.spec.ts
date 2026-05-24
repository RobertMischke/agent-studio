import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJobApi(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': 'local-default' },
  });
}

async function cleanup(prefix: string, watchPath: string): Promise<void> {
  const all = await api<Array<{ id: string; watchPath: string }>>('/api/jobs?includeFixtures=true');
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJobApi(j.id, j.watchPath).catch(() => {})));
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
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');

      // The VS-Code layout opens on the Welcome tab for a fresh
      // browser context. Click the project button in the welcome card
      // to open its board tab.
      const welcome = page.locator('[data-testid="studio-welcome"]');
      if (await welcome.isVisible({ timeout: 2_000 }).catch(() => false)) {
        const projectBtn = welcome.locator('.studio-welcome__project')
          .filter({ hasText: wp.name });
        await projectBtn.click();
      }
      await page.waitForTimeout(2000);

      const card = page.locator('app-job-card')
        .filter({ hasText: 'Delete test card' });

      await expect(card).toBeVisible({ timeout: 10_000 });

      await card.hover();
      const deleteBtn = card.locator('[data-testid="job-card-delete"]');
      await expect(deleteBtn).toBeVisible({ timeout: 3_000 });

      await deleteBtn.click();

      const dialog = page.locator('[data-testid="confirm-dialog"]');
      await expect(dialog).toBeVisible({ timeout: 3_000 });

      const dialogMessage = page.locator('[data-testid="confirm-dialog-message"]');
      await expect(dialogMessage).toContainText('removes the job folder');

      // Take a screenshot of the confirm dialog
      await page.screenshot({ path: 'test-results/card-delete-confirm-dialog.png' });

      // Cancel the dialog so we don't actually delete
      const cancelBtn = page.locator('[data-testid="confirm-dialog-cancel"]');
      await cancelBtn.click();

      await expect(dialog).not.toBeVisible({ timeout: 3_000 });

      // Card should still be on the board after cancel
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

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const welcome = page.locator('[data-testid="studio-welcome"]');
    if (await welcome.isVisible({ timeout: 2_000 }).catch(() => false)) {
      const projectBtn = welcome.locator('.studio-welcome__project')
        .filter({ hasText: wp.name });
      await projectBtn.click();
    }
    await page.waitForTimeout(2000);

    const card = page.locator('app-job-card')
      .filter({ hasText: 'Delete confirm test' });

    await expect(card).toBeVisible({ timeout: 10_000 });

    await card.hover();
    const deleteBtn = card.locator('[data-testid="job-card-delete"]');
    await expect(deleteBtn).toBeVisible({ timeout: 3_000 });

    await deleteBtn.click();

    const dialog = page.locator('[data-testid="confirm-dialog"]');
    await expect(dialog).toBeVisible({ timeout: 3_000 });

    // Confirm the deletion
    const confirmBtn = page.locator('[data-testid="confirm-dialog-confirm"]');
    await confirmBtn.click();

    // Dialog should close
    await expect(dialog).not.toBeVisible({ timeout: 3_000 });

    // Card should disappear from the board
    await expect(card).not.toBeVisible({ timeout: 10_000 });

    await page.screenshot({ path: 'test-results/card-delete-after-confirm.png' });
  });
});
