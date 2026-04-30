import { test, expect } from '@playwright/test';
import { api } from './helpers/api';
import { createJob, moveJob } from './helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/**
 * When a task is in progress (state "3-progress") the detail view's inspector
 * must default to the Aktivität (activity) tab so the user sees live CLI
 * output rather than a stale protocol from an earlier run.
 */
test.describe('Detail inspector — default tab', () => {
  test('in-progress job defaults to the Aktivität tab', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `default-tab-progress-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# In-progress default tab test',
      targetState: '2-ready'
    });
    await moveJob(job.id, watchPath, '3-progress');

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const activityTab = page.getByTestId('inspector-tab-activity');
      const protocolTab = page.getByTestId('inspector-tab-protocol');
      await expect(activityTab).toBeVisible({ timeout: 10_000 });

      // Activity tab must be the active one for an in-progress job.
      await expect(activityTab).toHaveClass(/inspector__tab--active/);
      await expect(protocolTab).not.toHaveClass(/inspector__tab--active/);

      await page.screenshot({
        path: 'test-results/inspector-tab-default-progress.png',
        fullPage: false
      });
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      );
    }
  });

  test('non-progress job without summary still defaults to Aktivität', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `default-tab-ready-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Ready default tab test',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const activityTab = page.getByTestId('inspector-tab-activity');
      await expect(activityTab).toBeVisible({ timeout: 10_000 });

      // No status.md yet → activity is the only meaningful tab to land on.
      await expect(activityTab).toHaveClass(/inspector__tab--active/);
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      );
    }
  });
});
