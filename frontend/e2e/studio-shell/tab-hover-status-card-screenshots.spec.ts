import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, getJob, waitForJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => { /* best-effort cleanup */ });
}

async function seedTab(page: Page, jobKey: string): Promise<void> {
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
  await page.evaluate(({ jobKey: jk }) => {
    const tab = { kind: 'task', jobKey: jk };
    const payload = { v: 1, tabs: [tab], activeKey: `task:${jk}` };
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
  }, { jobKey });
}

/**
 * Evidence screenshots for the bug `bug-explorer-open-tabs-alignment-and-hover-popover-for-truncated-titles-reusable-status-card`.
 * The functional assertions live in `tab-hover-status-card.spec.ts`; this
 * file only captures the before/after-style imagery the task report calls for.
 */
test.describe('Open-Tabs hover — evidence screenshots', () => {
  test.setTimeout(60_000);

  for (const theme of ['dark', 'light'] as const) {
    test(`${theme}: open-tabs alignment + hover popover`, async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 900 });

      const wp = await pickWatchPath();
      const id = `e2e-screenshot-${theme}-${Date.now()}`;
      const title = 'A very long task title that the explorer column will absolutely truncate with ellipsis';
      await createJob({ id, title, watchPath: wp.path, targetState: '2-ready', fixture: false });

      try {
        await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
        const job = await getJob(id, wp.path);
        await seedTab(page, job.jobKey);

        await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(wp.path)}`);
        await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });

        // Force the requested theme.
        await page.evaluate((t) => {
          localStorage.setItem('atp.studio.theme', t);
          document.documentElement.dataset['studioTheme'] = t;
        }, theme);
        // Give the theme CSS one tick to settle.
        await page.waitForTimeout(150);

        const openTabsHead = page.getByTestId('studio-explorer-open-tabs-head');
        await expect(openTabsHead).toBeVisible({ timeout: 5_000 });
        if (await openTabsHead.getAttribute('aria-expanded') === 'false') {
          await openTabsHead.click();
        }
        const tab = page.locator('[data-testid^="studio-explorer-open-tab-"]').filter({ hasText: title.slice(0, 32) }).first();
        await expect(tab).toBeVisible({ timeout: 15_000 });

        // Screenshot the alignment first (no popover, just the sidebar).
        const sidebar = page.getByTestId('studio-sidebar');
        await sidebar.screenshot({ path: `test-results/open-tabs-alignment-${theme}.png` });

        // Now open the popover and capture it.
        await tab.hover();
        const popover = page.getByTestId('task-status-card-popover');
        await expect(popover).toBeVisible({ timeout: 3_000 });
        await page.waitForTimeout(150);
        await page.screenshot({ path: `test-results/open-tabs-hover-card-${theme}.png` });
      } finally {
        await deleteJob(id, wp.path);
      }
    });
  }
});
