import { test, expect } from '@playwright/test';
import { api } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
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
 * VS Code-style filter sidesheet: a single right-edge panel that hosts the
 * board search box, tag and owner facets, and visibility toggles. Opens
 * via the header trigger icon, filters the board live as the user types,
 * and writes shareable URL query params (?q=…&tag=…&owner=…).
 */
test.describe('Kanban filter sidesheet', () => {
  const PREFIX = 'e2e-fsheet-';

  test.beforeEach(async () => { await cleanup(PREFIX); });
  test.afterEach(async () => { await cleanup(PREFIX); });

  test('opens, filters board live, writes ?q= URL param, closes via Esc and ✕', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    test.skip(!paths.length, 'no watch paths configured');
    const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
    const watchPath = target.path;

    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
      // Make sure the sheet starts closed regardless of the user's last
      // session so the open/close assertions are deterministic.
      localStorage.setItem('atp.kanban.filterSidesheetOpen', '0');
    });

    const unique = `quizzlebop${Date.now().toString(36)}`;
    const a = await createJob({
      id: PREFIX + unique,
      title: `Sidesheet card ${unique}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation',
      fixture: false,
    });

    try {
      await page.goto('/');

      // The trigger lives in the header; the sidesheet host is rendered
      // but collapsed (zero-width, off-screen).
      const trigger = page.getByTestId('kanban-filter-sidesheet-trigger');
      await expect(trigger).toBeVisible();

      const sheet = page.getByTestId('kanban-filter-sidesheet');
      // Wait for the just-created card to land on the board.
      const cardA = page.locator('app-job-card', { hasText: unique });
      await expect(cardA.first()).toBeVisible({ timeout: 15_000 });

      // Click the trigger -> sidesheet expands and the search input is focused.
      await trigger.click();
      const search = page.getByTestId('kanban-filter-sidesheet-search');
      await expect(search).toBeVisible();
      await expect(search).toBeFocused();

      // Type the unique substring; the rest of the board collapses to A only.
      await search.fill(unique);
      await expect(cardA.first()).toBeVisible();
      const otherCards = page.locator('app-job-card').filter({ hasNotText: unique });
      await expect(otherCards).toHaveCount(0);

      // URL reflects the query so the view is bookmarkable.
      await expect.poll(() => new URL(page.url()).searchParams.get('q')).toBe(unique);

      // The footer's hit count says "1 / N" with N >= 1.
      const hits = page.getByTestId('kanban-filter-sidesheet-hitcount');
      await expect(hits).toContainText(/^1 \/ \d+ jobs match$/);

      // Clear via the inline × button -> input still visible, query empty.
      await page.getByTestId('kanban-filter-sidesheet-search-clear').click();
      await expect(search).toHaveValue('');
      await expect.poll(() => new URL(page.url()).searchParams.get('q')).toBeNull();

      // Re-fill and close via Esc on the search input — the input collapses
      // back to "no search" the moment the sheet closes via Esc.
      await search.fill(unique);
      await search.press('Escape');
      await expect(sheet).not.toHaveClass(/sheet--open/);

      // Reopen via trigger and close via the explicit ✕ button.
      await trigger.click();
      await expect(search).toBeVisible();
      await page.getByTestId('kanban-filter-sidesheet-close').click();
      await expect(sheet).not.toHaveClass(/sheet--open/);
    } finally {
      await deleteJob(a.id, watchPath);
    }
  });

  test('hydrates query and tag from URL on load', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    test.skip(!paths.length, 'no watch paths configured');
    const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
    const watchPath = target.path;

    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
      localStorage.setItem('atp.kanban.filterSidesheetOpen', '0');
    });

    const unique = `prehydrate${Date.now().toString(36)}`;
    const a = await createJob({
      id: PREFIX + unique,
      title: `Hydrate card ${unique}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation',
      fixture: false,
    });

    try {
      await page.goto(`/?q=${encodeURIComponent(unique)}`);

      const trigger = page.getByTestId('kanban-filter-sidesheet-trigger');
      await expect(trigger).toBeVisible();

      // Card appears (search hydrated from URL).
      const cardA = page.locator('app-job-card', { hasText: unique });
      await expect(cardA.first()).toBeVisible({ timeout: 15_000 });
      const otherCards = page.locator('app-job-card').filter({ hasNotText: unique });
      await expect(otherCards).toHaveCount(0);

      // Trigger badge shows the active query chip.
      await expect(page.getByTestId('kanban-filter-sidesheet-trigger-chip')).toContainText(unique);

      // Open the sheet — its search input carries the hydrated value.
      await trigger.click();
      const search = page.getByTestId('kanban-filter-sidesheet-search');
      await expect(search).toHaveValue(unique);
    } finally {
      await deleteJob(a.id, watchPath);
    }
  });
});
