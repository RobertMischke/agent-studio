import { test, expect } from '@playwright/test';

/**
 * Regression test for the cross-project counter "leak" described in the
 * bug task: clicking a different project chip while another project is
 * active must switch the visible filter to the clicked project (single-
 * select), not stack it on top. Pre-fix the chip strip was a pure toggle
 * which, on a workspace where the second project has near-zero jobs,
 * made the counters look frozen at the first project's values. With the
 * fix, clicking Lotta after ATP replaces ATP, lane counters drop to the
 * second project's jobs, and Ctrl/Cmd+click stays additive so the
 * power-user multi-select case is still reachable.
 */

interface WatchPath { name: string; path: string }

const PROJECTS_ENDPOINT = '/api/watch-paths';

async function readLaneCount(page: import('@playwright/test').Page, state: string): Promise<number> {
  const lane = page.locator(`[data-testid="lane-${state}"]`);
  if (!(await lane.count())) return 0;
  const txt = (await lane.locator('.column__count').first().textContent()) ?? '0';
  return parseInt(txt.trim(), 10) || 0;
}

test.describe('project chip strip: single-select switch', () => {
  test('clicking a different project replaces, not stacks, the active filter', async ({ page, request }) => {
    const projects = (await (await request.get(PROJECTS_ENDPOINT)).json()) as WatchPath[];
    if (projects.length < 2) test.skip(true, 'Needs at least two watched projects');

    const [first, second] = projects;

    await page.goto('/');
    await page.evaluate(() => {
      localStorage.removeItem('activeProjects');
      localStorage.removeItem('collapsedLanes');
      location.hash = '';
    });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const firstChip = page.getByTestId(`project-filter-${first.name}`);
    const secondChip = page.getByTestId(`project-filter-${second.name}`);
    await expect(firstChip).toBeVisible();
    await expect(secondChip).toBeVisible();

    // Snapshot the full-board counts so we can recognise an "everything"
    // view later.
    const fullArchive = await readLaneCount(page, '7-archive');
    expect(fullArchive).toBeGreaterThan(0);

    await firstChip.click();
    await page.waitForTimeout(300);
    const firstOnlyArchive = await readLaneCount(page, '7-archive');
    await expect(firstChip).toHaveClass(/filter-chip--active/);
    await expect(secondChip).not.toHaveClass(/filter-chip--active/);

    // Switch to second project. Without Ctrl/Cmd this must replace the
    // selection, not stack it - the lane counter for archive must change
    // from "first project only" to "second project only".
    await secondChip.click();
    await page.waitForTimeout(300);
    await expect(secondChip).toHaveClass(/filter-chip--active/);
    await expect(firstChip).not.toHaveClass(/filter-chip--active/, {
      timeout: 2000,
    });
    const secondOnlyArchive = await readLaneCount(page, '7-archive');
    expect(secondOnlyArchive).not.toBe(firstOnlyArchive);
    expect(secondOnlyArchive).not.toBe(fullArchive);

    // Re-enable additive multi-select with Ctrl/Cmd+click and confirm
    // the legacy power-user path still works: holding the modifier adds
    // the first project back without removing the second.
    await firstChip.click({ modifiers: ['ControlOrMeta'] });
    await page.waitForTimeout(300);
    await expect(firstChip).toHaveClass(/filter-chip--active/);
    await expect(secondChip).toHaveClass(/filter-chip--active/);
    const bothActiveArchive = await readLaneCount(page, '7-archive');
    expect(bothActiveArchive).toBeGreaterThanOrEqual(secondOnlyArchive);

    // Clicking the sole-active chip with no modifier clears the filter,
    // mirroring the toggle-off intuition (returns to "all projects").
    // First peel the second project off via Ctrl+click, then plain-click
    // the remaining one.
    await secondChip.click({ modifiers: ['ControlOrMeta'] });
    await page.waitForTimeout(300);
    await expect(secondChip).not.toHaveClass(/filter-chip--active/);
    await firstChip.click();
    await page.waitForTimeout(300);
    await expect(firstChip).not.toHaveClass(/filter-chip--active/);
    const noFilterArchive = await readLaneCount(page, '7-archive');
    expect(noFilterArchive).toBe(fullArchive);
  });
});
