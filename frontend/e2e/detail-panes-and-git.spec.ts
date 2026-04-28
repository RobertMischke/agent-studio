import { test, expect } from '@playwright/test';
import { api } from './helpers/api';
import { createJob } from './helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

test.describe('Detail view — 3-pane layout + Git view', () => {
  test('toolbar toggles each panel and persists across reload', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `panes-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Pane test',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-prompt')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('pane-protocol')).toBeVisible();
      // Git pane is hidden by default — keeps the layout calm for new users.
      await expect(page.getByTestId('pane-git')).toHaveCount(0);

      // Show Git via the toolbar.
      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();

      // Hide the prompt pane.
      await page.getByTestId('pane-toggle-prompt').click();
      await expect(page.getByTestId('pane-prompt')).toHaveCount(0);

      // Reload — visibility must persist via localStorage.
      await page.reload();
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('pane-git')).toBeVisible();
      await expect(page.getByTestId('pane-prompt')).toHaveCount(0);

      // Restore prompt for the next user — leaves localStorage in a sane state.
      await page.getByTestId('pane-toggle-prompt').click();
      await page.getByTestId('pane-toggle-git').click();
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('Git view shows file count and supports an empty working tree', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `git-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Git view test',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });

      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();

      // Either we get a file count (repo dirty) or "Working tree clean" / "Not a
      // git repository" / loading state. All three are acceptable end-states; we
      // just need to confirm the panel rendered without erroring.
      const count = page.getByTestId('git-files-count');
      const empty = page.locator('.git-view__empty');
      await expect(count.or(empty).first()).toBeVisible({ timeout: 10_000 });
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
