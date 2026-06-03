import { test, expect, type Page } from '@playwright/test';

/**
 * ASS-606/607/620/621/657 follow-up: a project node in the Workspaces
 * Explorer no longer breaks out into per-lane children (backlog / active /
 * human review / archive). Expanding a project now reveals exactly two
 * links — "Board" (the project's kanban) and "Project View" (the hub).
 * Lanes stay reachable from the board itself.
 *
 * Runs against the live backend's project list; short-circuits when the
 * configured board has no projects.
 */

async function gotoStudio(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 10_000 });
}

/** Expand the first project row and return its locator + name. */
async function expandFirstProject(page: Page): Promise<{ row: ReturnType<Page['locator']>; name: string } | null> {
  const row = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
  if ((await row.count()) === 0) return null;
  const name = (await row.getAttribute('data-project-name')) ?? '';
  const label = row.locator('button.tree-row').first();
  const children = row.locator('.studio-tree-children');
  if (!(await children.count()) || !(await children.isVisible())) {
    await label.click();
    await expect(children).toBeVisible({ timeout: 3_000 });
  }
  return { row, name };
}

test.describe('Explorer · project links to Board + Project View only', () => {
  test('expanded project shows exactly Board and Project View, no lane rows', async ({ page }) => {
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — project-link contract skipped.');
      return;
    }
    const { row, name } = expanded;
    const children = row.locator('.studio-tree-children');

    // Exactly two child rows.
    const rows = children.locator('.tree-row');
    await expect(rows).toHaveCount(2);

    // They are Board + Project View, addressable by their stable testids.
    await expect(page.getByTestId(`studio-explorer-project-board-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-view-${name}`)).toBeVisible();

    // The retired per-lane labels must not appear under the project node.
    const childText = (await children.innerText()).toLowerCase();
    for (const gone of ['backlog', 'active', 'human review', 'archive', 'project hub']) {
      expect(childText).not.toContain(gone);
    }
    expect(childText).toContain('board');
    expect(childText).toContain('project view');
  });

  test('"Board" opens the project kanban', async ({ page }) => {
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — Board-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-board-${expanded.name}`).click();
    // A project board tab becomes active; its breadcrumb leaf is the board,
    // never "Project Hub".
    await expect(page.getByTestId('studio-titlebar-active-tab')).not.toHaveText('Project Hub', { timeout: 5_000 });
  });

  test('"Project View" opens the Project Hub', async ({ page }) => {
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — Project-View-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-view-${expanded.name}`).click();
    await expect(page.getByTestId('studio-titlebar-active-tab')).toHaveText('Project Hub', { timeout: 5_000 });
  });
});
