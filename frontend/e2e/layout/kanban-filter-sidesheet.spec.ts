import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await api(
    `/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' },
  ).catch(() => {});
}

async function cleanup(prefix: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath)));
}

/**
 * F25: the kanban filter UI lives only in the activity-bar "Filters"
 * panel on the left. The previous right-edge slide-in sheet and the
 * tab-action / header filter triggers are gone. The `/` keyboard
 * shortcut opens the panel and focuses the inline search input.
 */
test.describe('Kanban filter panel (activity bar)', () => {
  const PREFIX = 'e2e-fsheet-';

  test.beforeEach(async () => { await cleanup(PREFIX); });
  test.afterEach(async () => { await cleanup(PREFIX); });

  test('filters board live, writes ?q= URL param, opens via "/" shortcut', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    test.skip(!paths.length, 'no watch paths configured');
    const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
    const watchPath = target.path;

    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
      // Open an "All projects" board tab so the kanban actually renders;
      // the studio shell otherwise sits on the welcome screen with no
      // cards mounted.
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }));
    });

    const unique = `quizzlebop${Date.now().toString(36)}`;
    const a = await createJob({
      id: PREFIX + unique,
      title: `Filter card ${unique}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation',
      fixture: false,
    });

    try {
      await page.goto('/');

      // The right-edge filter sheet trigger is gone: neither the
      // tab-action button nor the legacy header trigger exist.
      await expect(page.getByTestId('studio-board-filter-trigger')).toHaveCount(0);
      await expect(page.getByTestId('kanban-filter-sidesheet-trigger')).toHaveCount(0);

      // Activity-bar Filters icon opens the inline panel.
      const filtersIcon = page.locator('[data-testid="studio-activity-bar"] [data-panel="filters"]');
      await expect(filtersIcon).toBeVisible({ timeout: 10_000 });
      const inlinePanel = page.getByTestId('kanban-filter-sidesheet-inline');
      if (!(await inlinePanel.isVisible().catch(() => false))) {
        await filtersIcon.click();
      }
      await expect(inlinePanel).toBeVisible({ timeout: 5_000 });

      // No right-edge filter sidesheet host in the DOM (only the
      // inline `studioFilterPanel`-projected instance survives).
      const sidesheetHosts = page.locator('app-kanban-filter-sidesheet');
      const inlineHost = sidesheetHosts.filter({ has: page.getByTestId('kanban-filter-sidesheet-inline') });
      await expect(sidesheetHosts).toHaveCount(await inlineHost.count());

      const search = page.getByTestId('kanban-filter-sidesheet-search');
      await expect(search).toBeVisible();

      // Wait for the just-created card to land on the board.
      const cardA = page.locator('app-job-card', { hasText: unique });
      await expect(cardA.first()).toBeVisible({ timeout: 15_000 });

      // Type the unique substring; the rest of the board collapses to A only.
      await search.fill(unique);
      await expect(cardA.first()).toBeVisible();
      const otherCards = page.locator('app-job-card').filter({ hasNotText: unique });
      await expect(otherCards).toHaveCount(0);

      // URL reflects the query so the view is bookmarkable.
      await expect.poll(() => new URL(page.url()).searchParams.get('q')).toBe(unique);

      // Clear via the inline × button -> input still visible, query empty.
      await page.getByTestId('kanban-filter-sidesheet-search-clear').click();
      await expect(search).toHaveValue('');
      await expect.poll(() => new URL(page.url()).searchParams.get('q')).toBeNull();

      // `/` keyboard shortcut focuses the search input even after blur.
      await page.locator('body').click();
      await page.keyboard.press('/');
      await expect(search).toBeFocused();
    } finally {
      await deleteJob(a.id, watchPath);
    }
  });

  test('hydrates query from URL on load', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    test.skip(!paths.length, 'no watch paths configured');
    const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
    const watchPath = target.path;

    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
      // Open an "All projects" board tab so the kanban actually renders;
      // the studio shell otherwise sits on the welcome screen with no
      // cards mounted.
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }));
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

      // Card appears (search hydrated from URL).
      const cardA = page.locator('app-job-card', { hasText: unique });
      await expect(cardA.first()).toBeVisible({ timeout: 15_000 });
      const otherCards = page.locator('app-job-card').filter({ hasNotText: unique });
      await expect(otherCards).toHaveCount(0);

      // Open the activity-bar Filters panel and confirm the search
      // input mirrors the hydrated URL query.
      const filtersIcon = page.locator('[data-testid="studio-activity-bar"] [data-panel="filters"]');
      const inlinePanel = page.getByTestId('kanban-filter-sidesheet-inline');
      if (!(await inlinePanel.isVisible().catch(() => false))) {
        await filtersIcon.click();
      }
      await expect(page.getByTestId('kanban-filter-sidesheet-search')).toHaveValue(unique);
    } finally {
      await deleteJob(a.id, watchPath);
    }
  });
});
