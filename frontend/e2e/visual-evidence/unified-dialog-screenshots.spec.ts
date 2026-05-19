import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';
import * as path from 'path';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJobApi(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID?.trim() || 'local-default' }
  });
}

const SCREENSHOT_DIR = process.env.UNIFIED_DIALOG_SCREENSHOT_DIR
  ?? path.resolve(__dirname, '../../docs-snapshots/unified-confirm-and-notification-modals');

/**
 * Visual evidence for the unified-confirm-and-notification-modals task.
 * Each test focuses on one state of the new component family and saves a
 * full-viewport PNG so the unified look (Catppuccin-inspired dark panel,
 * shared eyebrow / title / actions skeleton) is reviewable in the chat
 * reply without booting the app.
 */
test.describe('Unified confirm + notify visuals', () => {
  test('delete-confirm dialog (danger variant)', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = `e2e-shot-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
    const job = await createJob({
      id,
      title: `Screenshot fixture ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
      promptMarkdown: 'Visual evidence fixture; safe to delete.',
      fixture: false,
    });
    try {
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      // Give vite/HMR time to settle before interacting.
      await page.waitForTimeout(1500);

      const card = page.locator(`[data-testid="job-card"]`, { hasText: job.id });
      await expect(card).toBeVisible({ timeout: 15_000 });
      await card.scrollIntoViewIfNeeded();
      await card.hover({ force: true });
      const trash = card.getByTestId('job-card-delete');
      await expect(trash).toBeVisible({ timeout: 5_000 });
      await trash.click({ force: true });

      const dialog = page.getByTestId('confirm-dialog-panel');
      await expect(dialog).toBeVisible({ timeout: 5_000 });
      await page.screenshot({
        path: path.join(SCREENSHOT_DIR, 'confirm-dialog-danger.png'),
        fullPage: false,
      });
      await page.getByTestId('confirm-dialog-cancel').click();
    } finally {
      await deleteJobApi(job.id, wp.path).catch(() => {});
    }
  });

  test('notification stack — success / info / warning / error', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1500);

    // Drive the notification stack directly via window.ng to render every
    // kind side by side without depending on a particular feature flow.
    await page.waitForFunction(() => Boolean((window as any).__notifications));
    await page.evaluate(() => {
      const svc = (window as any).__notifications;
      svc.success('Task deleted', 'Done');
      svc.info('Lane cleared.');
      svc.warning('Three retries left before fallback.', 'Quota low');
      svc.error('Backend returned 500. Run ./api.sh restart.', 'Request failed');
    });

    const stack = page.getByTestId('notification-stack');
    await expect(stack).toBeVisible({ timeout: 5_000 });
    // Wait for all four to mount.
    await expect(page.locator('.app-notify')).toHaveCount(4, { timeout: 5_000 });
    await page.screenshot({
      path: path.join(SCREENSHOT_DIR, 'notification-stack.png'),
      fullPage: false,
    });
  });

  test('confirm dialog (primary variant) — chat jump-to-start', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1500);

    await page.waitForFunction(() => Boolean((window as any).__confirmDialog));
    await page.evaluate(() => {
      const svc = (window as any).__confirmDialog;
      void svc.confirm({
        title: 'Load entire chat history?',
        message: 'Load all 12,438 messages? This may take a moment.',
        confirmLabel: 'Load all',
        cancelLabel: 'Cancel',
        kind: 'primary',
      });
    });

    const dialog = page.getByTestId('confirm-dialog-panel');
    await expect(dialog).toBeVisible({ timeout: 5_000 });
    await page.screenshot({
      path: path.join(SCREENSHOT_DIR, 'confirm-dialog-primary.png'),
      fullPage: false,
    });
    await page.getByTestId('confirm-dialog-cancel').click();
  });
});
