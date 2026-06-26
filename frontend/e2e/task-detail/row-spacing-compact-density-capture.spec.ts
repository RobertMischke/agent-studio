import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, waitForJob } from '../helpers/jobs';

/**
 * Capture-only spec for the density polish task. Produces "after" screenshots
 * of the Overview tab in dark + light theme so the operator can eyeball
 * the new spacing. The numbers are asserted by
 * row-spacing-compact-density.spec.ts; this file is just evidence.
 *
 * Output (test-results/) is also copied into the job folder's `results/` by
 * the job-evidence promotion step.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => { /* best-effort */ });
}

function uid(suffix: string) {
  return `e2e-density-capture-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function openTaskDirectly(page: Page, jobId: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 10_000 });
}

test.describe('Row density — overview screenshots', () => {
  test('captures Overview tab in dark + light theme', async ({ page }, testInfo) => {
    const wp = await pickWatchPath();
    const id = uid('overview');
    await createJob({ id, title: id, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await page.setViewportSize({ width: 1400, height: 900 });
      await openTaskDirectly(page, id, wp.path);

      const overview = page.locator('app-overview-pane');
      await expect(overview).toBeVisible();

      // Dark first (default).
      const darkPng = await overview.screenshot();
      await testInfo.attach('overview-after-dark.png', { body: darkPng, contentType: 'image/png' });
      await page.screenshot({ path: 'test-results/density-after-dark.png', fullPage: false });

      // Switch to light theme via the documented data attribute.
      await page.evaluate(() => {
        document.documentElement.setAttribute('data-studio-theme', 'light');
      });
      await page.waitForTimeout(150);

      const lightPng = await overview.screenshot();
      await testInfo.attach('overview-after-light.png', { body: lightPng, contentType: 'image/png' });
      await page.screenshot({ path: 'test-results/density-after-light.png', fullPage: false });
    } finally {
      await deleteJob(id, wp.path);
    }
  });
});
