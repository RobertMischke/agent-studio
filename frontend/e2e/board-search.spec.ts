import { test, expect } from '@playwright/test';
import { api } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  // Use the api helper so the X-Client-Id header is attached; raw fetch
  // would 4xx out under the post-multi-client backend contract.
  await api(
    `/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' },
  ).catch(() => {});
}

async function cleanup(prefix: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath)));
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
    // Prefer the agent-taskboard project (the dev's own checkout) so the
    // card lands under whatever owner / lane filters the user has active
    // by default. Fall back to the first path when only one is configured.
    const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
    const watchPath = target.path;

    // Clear the persisted project filter so the card we create is visible
    // regardless of which chips the developer last toggled. activeProjects
    // == [] means "show all".
    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
    });

    const uniqueA = `zorblax${Date.now().toString(36)}`;
    const a = await createJob({
      id: PREFIX + uniqueA,
      title: `Card A about ${uniqueA}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation',
      // fixture jobs are hidden from /api/jobs/grouped so they would not
      // render on the kanban; we need a real card for this spec.
      fixture: false,
    });

    try {
      await page.goto('/');
      const search = page.getByTestId('board-search-input');
      await expect(search).toBeVisible();

      const cardA = page.locator('app-job-card', { hasText: uniqueA });
      // Wait for the polling cycle to fold the freshly-created card in.
      await expect(cardA.first()).toBeVisible({ timeout: 15_000 });

      // Type the unique substring; the rest of the board collapses to A only.
      await search.fill(uniqueA);
      await expect(cardA.first()).toBeVisible();
      // Every other card on the board is gone.
      const otherCards = page.locator('app-job-card').filter({ hasNotText: uniqueA });
      await expect(otherCards).toHaveCount(0);

      // The × button clears the input and the rest of the board returns.
      await page.getByTestId('board-search-clear').click();
      await expect(search).toHaveValue('');
      await expect(cardA.first()).toBeVisible();

      // A clearly non-matching query empties every lane.
      await search.fill('zzz-no-such-token-zzz');
      await expect(page.locator('app-job-card')).toHaveCount(0);

      // Escape clears via keyboard.
      await search.press('Escape');
      await expect(search).toHaveValue('');
      await expect(cardA.first()).toBeVisible();
    } finally {
      await deleteJob(a.id, watchPath);
    }
  });
});
