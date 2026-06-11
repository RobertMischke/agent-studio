import { test, expect, type Page } from '@playwright/test';

/**
 * ASS-658/ASS-597: a project node in the Workspaces Explorer exposes exactly
 * four project-scoped links — "Board" (the kanban), "Project Hub", "Backlog"
 * (triage) and "Epics" (overview). The retired per-lane children (active /
 * human review / archive) stay gone, and Backlog / Epics open scoped to the
 * clicked project rather than the global rollup.
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

test.describe('Explorer · project links to Board / Project Hub / Backlog / Epics', () => {
  test('expanded project shows exactly the four project links, no lane rows', async ({ page }) => {
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — project-link contract skipped.');
      return;
    }
    const { row, name } = expanded;
    const children = row.locator('.studio-tree-children');

    // Exactly four child rows.
    const rows = children.locator('.tree-row');
    await expect(rows).toHaveCount(4);

    // Each is addressable by its stable testid.
    await expect(page.getByTestId(`studio-explorer-project-board-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-hub-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-backlog-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-epics-${name}`)).toBeVisible();

    // The retired per-lane labels must not appear under the project node.
    const childText = (await children.innerText()).toLowerCase();
    for (const gone of ['active', 'human review', 'archive', 'project view']) {
      expect(childText).not.toContain(gone);
    }
    expect(childText).toContain('board');
    expect(childText).toContain('project hub');
    expect(childText).toContain('backlog');
    expect(childText).toContain('epics');
  });

  test('"Board" opens the project kanban', async ({ page }) => {
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — Board-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-board-${expanded.name}`).click();
    await expect(page.getByRole('tab', { name: `${expanded.name} · Board` })).toHaveAttribute('aria-selected', 'true', { timeout: 5_000 });

    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText(expanded.name);
    await expect(page.getByTestId('studio-titlebar-workspace')).toHaveCount(0);
    await expect(page.getByTestId('studio-titlebar-active-tab')).toHaveCount(0);
    await expect(page.getByTestId('studio-titlebar-crumbs')).not.toContainText('Board');
  });

  test('"Project Hub" opens the Project Hub', async ({ page }) => {
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — Project-Hub-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-hub-${expanded.name}`).click();
    await expect(page.getByRole('tab', { name: `${expanded.name} · Hub` })).toHaveAttribute('aria-selected', 'true', { timeout: 5_000 });
  });

  test('"Backlog" opens the backlog triage screen', async ({ page }) => {
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — Backlog-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-backlog-${expanded.name}`).click();
    await expect(page).toHaveURL(/#\/backlog/, { timeout: 5_000 });
    await expect(page.getByTestId('studio-ab-backlog')).toHaveClass(/studio-ab__btn--active/, { timeout: 5_000 });
  });
});
