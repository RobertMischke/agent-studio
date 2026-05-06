import { test, expect } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

async function cleanup(prefix: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

/**
 * The board search box (above the kanban columns) filters cards by a
 * substring match across every JobInfo field that's loaded for the
 * grouped view: title, id, project, agent, model, CLI, session, state,
 * owner, phase, type, tag ids. This spec drives the input, asserts that
 * hits and misses are reflected in the columns, and that the clear (×)
 * button and Escape key restore the full view.
 */
test.describe('Board search', () => {
  const PREFIX = 'e2e-search-';

  test.beforeEach(async () => { await cleanup(PREFIX); });
  test.afterEach(async () => { await cleanup(PREFIX); });

  test('filters tasks by unique title token, clear restores', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    test.skip(!paths.length, 'no watch paths configured');
    const watchPath = paths[0].path;

    const uniqueA = `zorblax${Date.now().toString(36)}`;
    const uniqueB = `quibsnap${Date.now().toString(36)}`;
    const a = await createJob({
      id: PREFIX + uniqueA,
      title: `Card A about ${uniqueA}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation',
    });
    const b = await createJob({
      id: PREFIX + uniqueB,
      title: `Card B about ${uniqueB}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation',
    });

    await page.goto('/');
    const search = page.getByTestId('board-search-input');
    await expect(search).toBeVisible();

    const cardA = page.locator('app-job-card', { hasText: uniqueA });
    const cardB = page.locator('app-job-card', { hasText: uniqueB });
    await expect(cardA).toBeVisible();
    await expect(cardB).toBeVisible();

    // Typing the unique substring of A leaves only A visible.
    await search.fill(uniqueA);
    await expect(cardA).toBeVisible();
    await expect(cardB).toHaveCount(0);

    // The × button clears the input and restores both cards.
    await page.getByTestId('board-search-clear').click();
    await expect(search).toHaveValue('');
    await expect(cardA).toBeVisible();
    await expect(cardB).toBeVisible();

    // A clearly non-matching query empties every lane.
    await search.fill('zzz-no-such-token-zzz');
    await expect(cardA).toHaveCount(0);
    await expect(cardB).toHaveCount(0);

    // Escape clears via keyboard.
    await search.press('Escape');
    await expect(search).toHaveValue('');
    await expect(cardA).toBeVisible();
    await expect(cardB).toBeVisible();

    // Cleanup is handled by afterEach, but be explicit about the IDs we created.
    await deleteJob(a.id, watchPath).catch(() => {});
    await deleteJob(b.id, watchPath).catch(() => {});
  });
});
