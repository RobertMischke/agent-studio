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
 * The header-mounted board search icon expands into the original search
 * input on click, exposes the same case-insensitive substring filter the
 * board-toolbar input drove before, and collapses back into a slim chip
 * (or icon) on Escape / blur. This spec exercises the expand / type /
 * collapse cycle, the chip survival path, the '/' shortcut, and the
 * context-gating rule that hides the icon outside the kanban view.
 */
test.describe('Board search (header icon)', () => {
  const PREFIX = 'e2e-search-';

  test.beforeEach(async () => { await cleanup(PREFIX); });
  test.afterEach(async () => { await cleanup(PREFIX); });

  test('icon expands to input, filters, Esc collapses, slash reopens', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    test.skip(!paths.length, 'no watch paths configured');
    // Prefer the agent-taskboard project (the dev's own checkout) so the
    // card lands under whatever owner / lane filters the user has active
    // by default. Fall back to the first path when only one is configured.
    const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
    const watchPath = target.path;

    // Clear the persisted project filter so the card we create is visible
    // regardless of which chips the developer last toggled.
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

      // The header carries the icon button; the input is NOT mounted yet.
      const icon = page.getByTestId('board-search-icon');
      await expect(icon).toBeVisible();
      await expect(page.getByTestId('board-search-input')).toHaveCount(0);

      const cardA = page.locator('app-job-card', { hasText: uniqueA });
      // Wait for the polling cycle to fold the freshly-created card in.
      await expect(cardA.first()).toBeVisible({ timeout: 15_000 });

      // Click the icon - the input mounts, takes focus, accepts a query.
      await icon.click();
      const search = page.getByTestId('board-search-input');
      await expect(search).toBeVisible();
      await expect(search).toBeFocused();

      // Type the unique substring; the rest of the board collapses to A only.
      await search.fill(uniqueA);
      await expect(cardA.first()).toBeVisible();
      const otherCards = page.locator('app-job-card').filter({ hasNotText: uniqueA });
      await expect(otherCards).toHaveCount(0);

      // Esc with a non-empty query collapses the input but keeps a chip
      // visible carrying the active query - the filter stays applied.
      await search.press('Escape');
      const chip = page.getByTestId('board-search-chip');
      await expect(chip).toBeVisible();
      await expect(chip).toContainText(uniqueA);
      // Filter still active: only A is on the board.
      await expect(cardA.first()).toBeVisible();
      await expect(otherCards).toHaveCount(0);

      // Click the chip - the input expands again with the query intact.
      await chip.click();
      await expect(search).toBeVisible();
      await expect(search).toHaveValue(uniqueA);

      // Clear via the × inside the expanded input - back to bare icon.
      await page.getByTestId('board-search-clear').click();
      await expect(page.getByTestId('board-search-input')).toHaveCount(0);
      await expect(page.getByTestId('board-search-chip')).toHaveCount(0);
      await expect(page.getByTestId('board-search-icon')).toBeVisible();
      // Full board returns.
      await expect(cardA.first()).toBeVisible();

      // The "/" shortcut anywhere on the kanban opens the search.
      await page.locator('body').press('/');
      await expect(page.getByTestId('board-search-input')).toBeVisible();
      await expect(page.getByTestId('board-search-input')).toBeFocused();

      // Esc with an empty query collapses straight to the icon.
      await page.getByTestId('board-search-input').press('Escape');
      await expect(page.getByTestId('board-search-input')).toHaveCount(0);
      await expect(page.getByTestId('board-search-icon')).toBeVisible();
    } finally {
      await deleteJob(a.id, watchPath);
    }
  });

  test('icon hides when a task detail page is open', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    test.skip(!paths.length, 'no watch paths configured');
    const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
    const watchPath = target.path;

    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
    });

    const unique = `ctxgate${Date.now().toString(36)}`;
    const j = await createJob({
      id: PREFIX + unique,
      title: `Detail-gate card ${unique}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation',
      fixture: false,
    });

    try {
      await page.goto('/');
      await expect(page.getByTestId('board-search-icon')).toBeVisible();

      // Open the task detail; the icon must disappear from the header.
      const card = page.locator('app-job-card', { hasText: unique }).first();
      await expect(card).toBeVisible({ timeout: 15_000 });
      await card.click();
      // Detail screen mounts a back-to-board control, signaling "in detail".
      await expect(page.getByTestId('back-to-board').first()).toBeVisible();
      await expect(page.getByTestId('board-search-icon')).toHaveCount(0);
      await expect(page.getByTestId('board-search-input')).toHaveCount(0);
      await expect(page.getByTestId('board-search-chip')).toHaveCount(0);
    } finally {
      await deleteJob(j.id, watchPath);
    }
  });
});
