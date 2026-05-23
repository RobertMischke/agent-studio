import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

test.describe('Detail view — compact command bar, pane maximize, collapsible task list', () => {
  test('command bar fits one row, panes maximize, task list collapses', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `compact-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Compact UI test',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      // Compact command bar: one row, no legacy "Command deck" / "Context Usage" headings.
      const bar = page.getByTestId('commandbar');
      await expect(bar).toBeVisible({ timeout: 10_000 });
      await expect(page.getByText('Command deck', { exact: false })).toHaveCount(0);
      await expect(page.getByText('/context usage', { exact: false })).toHaveCount(0);

      const barBox = await bar.boundingBox();
      // One slim row: keep it under ~80px tall regardless of viewport.
      expect(barBox?.height ?? 999).toBeLessThan(80);

      await page.screenshot({ path: 'test-results/compact-commandbar.png', fullPage: false });

      // Pane maximize button puts one pane full-width and hides others.
      await expect(page.getByTestId('pane-prompt')).toBeVisible();
      await expect(page.getByTestId('pane-protocol')).toBeVisible();

      await page.getByTestId('pane-protocol-header').getByTestId('pane-header-maximize').click();
      await expect(page.getByTestId('pane-protocol')).toBeVisible();
      await expect(page.getByTestId('pane-prompt')).toHaveCount(0);
      await page.screenshot({ path: 'test-results/protocol-maximized.png', fullPage: false });

      // Restore.
      await page.getByTestId('pane-protocol-header').getByTestId('pane-header-maximize').click();
      await expect(page.getByTestId('pane-prompt')).toBeVisible();

      // Show & maximize Git.
      await page.getByTestId('pane-toggle-git').click();
      await page.getByTestId('pane-maximize-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();
      await expect(page.getByTestId('pane-prompt')).toHaveCount(0);
      await expect(page.getByTestId('pane-protocol')).toHaveCount(0);
      await page.screenshot({ path: 'test-results/git-maximized.png', fullPage: false });
      await page.getByTestId('pane-maximize-git').click();

      // Task list collapse.
      await page.getByTestId('task-nav-collapse').click();
      await expect(page.getByTestId('task-nav-collapsed')).toBeVisible();
      await page.screenshot({ path: 'test-results/task-nav-collapsed.png', fullPage: false });
      await page.getByTestId('task-nav-expand').click();
      await expect(page.getByTestId('task-nav-collapsed')).toHaveCount(0);
    } finally {
      // Reset persisted layout state so other specs aren't surprised.
      await page.evaluate(() => {
        localStorage.removeItem('taskNavCollapsed');
        localStorage.removeItem('taskboard.panesVisible');
      });
    }
  });
});
