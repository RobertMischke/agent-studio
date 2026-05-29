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

function uid(suffix: string): string {
  return `e2e-tab-hover-card-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * Opens a task as a studio-shell tab. The `?job=...` URL alone only
 * hydrates the detail panel; the open-tabs list reads `StudioTabStateService`
 * which is persisted to `localStorage`. We seed that here so the task
 * appears in the Explorer's open-tabs section.
 *
 * `jobKey` is the canonical key the backend stamps on the JobInfo — we
 * fetch it here rather than reconstruct it, because path separators and
 * encoding can drift between client and server.
 */
async function openTaskInTab(page: Page, jobId: string, watchPath: string): Promise<void> {
  const job = await getJob(jobId, watchPath);
  const jobKey = job.jobKey;
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
  await page.evaluate(({ jobKey: jk }) => {
    const tab = { kind: 'task', jobKey: jk };
    const payload = { v: 1, tabs: [tab], activeKey: `task:${jk}` };
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
  }, { jobKey });
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
}

/**
 * Bug `bug-explorer-open-tabs-alignment-and-hover-popover-for-truncated-titles-reusable-status-card`:
 * the Explorer's "Open tabs" list truncates long task titles with no way to
 * see the full text; the operator wants a hover popover that uses a shared
 * `<app-task-status-card>` (also recycled inside the activity tab and the
 * board's compact task-card title hover).
 *
 * This spec covers the load-bearing flows:
 *   1. Open-Tabs row gets `--open-tab` class (Option A indent alignment with
 *      Workspace glyph column).
 *   2. Hover over a long open-tab title for >500 ms opens the
 *      `<app-task-status-card>` popover with title, project, lane.
 *   3. mouseleave hides the popover.
 */
test.describe('Open-Tabs hover → TaskStatusCard popover', () => {
  test.setTimeout(60_000);

  test('Open-Tabs row uses the shared open-tab indent (Option A)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });

    // Force at least one Hub tab so the Open-Tabs section is populated.
    const projectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
    if (await projectRow.count() === 0) {
      test.skip(true, 'No projects loaded — open-tabs alignment spec needs at least one project.');
      return;
    }
    await projectRow.locator('.studio-tree-row__hub-link').first().click();
    const tabRow = page.locator('[data-testid^="studio-explorer-open-tab-"]').first();
    await expect(tabRow).toBeVisible({ timeout: 5_000 });

    // The row carries the alignment-token class; padding-left resolves to
    // the same `--studio-explorer-row-glyph-x` (30 px by default) so it sits
    // buendig under the Workspace project glyph column.
    await expect(tabRow).toHaveClass(/studio-tree-row--open-tab/);
    const paddingLeft = await tabRow.evaluate((el) => getComputedStyle(el).paddingLeft);
    // The token resolves to 30 px on default theme; assert a reasonable
    // bracket so it does not regress to the chevron column (8 px) or the
    // child-row column (44 px).
    const px = parseFloat(paddingLeft);
    expect(px).toBeGreaterThanOrEqual(24);
    expect(px).toBeLessThanOrEqual(34);
  });

  test('hover over a truncated tab title opens the status card popover', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    const wp = await pickWatchPath();
    const id = uid('truncated');
    // Title long enough to overflow the explorer's narrow column.
    const title = 'A very long task title that the explorer column will absolutely truncate with ellipsis ' + id;
    // fixture=false so the job shows up in /api/jobs/grouped (the default
    // omits fixtures, which would leave findJob(jobKey) → null and the
    // popover directive would never fire). The finally block deletes it.
    await createJob({ id, title, watchPath: wp.path, targetState: '2-ready', fixture: false });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskInTab(page, id, wp.path);

      // Make sure the Open-Tabs section is expanded.
      const openTabsHead = page.getByTestId('studio-explorer-open-tabs-head');
      await expect(openTabsHead).toBeVisible({ timeout: 5_000 });
      if (await openTabsHead.getAttribute('aria-expanded') === 'false') {
        await openTabsHead.click();
      }

      // Wait until the open-tab actually displays the task TITLE (not its
      // path fallback) — that means JobService has polled grouped() and
      // findJob() now returns the JobInfo, so the popover directive's input
      // is non-null. Without this, hovering during the boot-time gap leaves
      // the directive with `[appTaskStatusPopover]="null"` and bails.
      const tab = page.locator('[data-testid^="studio-explorer-open-tab-"]').filter({ hasText: title.slice(0, 32) }).first();
      await expect(tab).toBeVisible({ timeout: 15_000 });

      // Move into the row and wait past the 500 ms open delay.
      await tab.hover();
      const popover = page.getByTestId('task-status-card-popover');
      await expect(popover).toBeVisible({ timeout: 3_000 });
      await expect(popover).toHaveClass(/task-status-card-host--visible/);
      await expect(popover).toHaveClass(/task-status-card-host--visible/);

      // Card carries the full title + project + lane.
      await expect(popover.getByTestId('task-status-card-title')).toHaveText(new RegExp(id));
      await expect(popover.getByTestId('task-status-card-project')).toContainText(wp.name);
      await expect(popover.getByTestId('task-status-card-lane')).toBeVisible();

      // Mouse out → card hides (we move into the title bar, well away from
      // both the tab and the popover host).
      await page.mouse.move(10, 10);
      await expect(popover).not.toHaveClass(/task-status-card-host--visible/, { timeout: 2_000 });
    } finally {
      await deleteJob(id, wp.path);
    }
  });
});
