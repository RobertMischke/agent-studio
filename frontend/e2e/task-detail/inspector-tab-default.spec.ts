import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';

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
      await expect(activityTab).toHaveClass(/pane-tab--active/);
      await expect(protocolTab).not.toHaveClass(/pane-tab--active/);

      await page.screenshot({
        path: 'test-results/inspector-tab-default-progress.png',
        fullPage: false
      });
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
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
      await expect(activityTab).toHaveClass(/pane-tab--active/);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      );
    }
  });

  /**
   * Regression: a job sitting in 2-ready/4-review with a stale status.md from
   * a previous run lands on Protokoll. When the runner picks it up (or the
   * user starts/continues), state flips to 3-progress while the detail view
   * is already open — the tab must demote to Aktivität so the live CLI log
   * is visible, instead of leaving the user staring at a stale summary.
   */
  test('open job demotes Protokoll → Aktivität when state transitions to 3-progress', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `default-tab-transition-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# State transition test',
      targetState: '2-ready'
    });

    // Seed a stale status.md so the detail view defaults to Protokoll on open.
    // The PUT /files endpoint only allows prompt.md, so we write to disk
    // directly — both the test runner and the backend are local processes.
    {
      const created = await getJob(job.id, watchPath);
      await writeFile(
        join(created.folderPath, 'status.md'),
        '# Stale protocol from a previous run\n'
      );
    }

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const activityTab = page.getByTestId('inspector-tab-activity');
      const protocolTab = page.getByTestId('inspector-tab-protocol');
      await expect(protocolTab).toBeVisible({ timeout: 10_000 });

      // Initial state: stale summary → Protokoll is active.
      await expect(protocolTab).toHaveClass(/pane-tab--active/);
      await expect(activityTab).not.toHaveClass(/pane-tab--active/);

      await page.screenshot({
        path: 'test-results/inspector-tab-transition-before.png',
        fullPage: false
      });

      // Simulate the runner picking the job up — state goes 2-ready → 3-progress
      // while the detail view is already open.
      await moveJob(job.id, watchPath, '3-progress');

      // Tab must auto-demote to Aktivität within the next detail-refresh tick.
      await expect(activityTab).toHaveClass(/pane-tab--active/, { timeout: 10_000 });
      await expect(protocolTab).not.toHaveClass(/pane-tab--active/);

      await page.screenshot({
        path: 'test-results/inspector-tab-transition-after.png',
        fullPage: false
      });
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      );
    }
  });

  /**
   * If the user has explicitly picked Protokoll, the auto-demote on state
   * transition must respect that choice and NOT switch them to Aktivität.
   * Mirrors the userTouchedInspectorTab guard on the activity → protocol side.
   */
  test('manual Protokoll choice survives a transition into 3-progress', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `default-tab-manual-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Manual tab choice test',
      targetState: '2-ready'
    });

    {
      const created = await getJob(job.id, watchPath);
      await writeFile(
        join(created.folderPath, 'status.md'),
        '# Stale protocol from a previous run\n'
      );
    }

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const activityTab = page.getByTestId('inspector-tab-activity');
      const protocolTab = page.getByTestId('inspector-tab-protocol');
      await expect(protocolTab).toBeVisible({ timeout: 10_000 });

      // User toggles to Aktivität and back to Protokoll — second click marks
      // the choice as user-driven.
      await activityTab.click();
      await expect(activityTab).toHaveClass(/pane-tab--active/);
      await protocolTab.click();
      await expect(protocolTab).toHaveClass(/pane-tab--active/);

      await moveJob(job.id, watchPath, '3-progress');

      // Manual choice must be respected — give the detail-effect a couple of
      // refresh cycles to (not) demote.
      await page.waitForTimeout(5_000);
      await expect(protocolTab).toHaveClass(/pane-tab--active/);
      await expect(activityTab).not.toHaveClass(/pane-tab--active/);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      );
    }
  });
});
