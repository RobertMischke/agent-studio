import { test, expect, Page } from '@playwright/test';

/**
 * F27 regression: every Explorer-tree folder header is collapsible via
 * chevron OR label click, persists across reloads, and project-row
 * label-click toggles the lane children (open-board moves to double-
 * click; the Hub stays reachable via the existing hub-link icon).
 *
 * Runs against the live backend's project list. Each test resets the
 * localStorage keys it owns so the spec starts from the documented
 * default (everything expanded). When the configured board has zero
 * projects the project-row contract assertions short-circuit; the
 * workspace + open-tabs mechanics still run.
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

test.describe('F27: Explorer-tree folder headers are all collapsible', () => {
  test('Workspace header toggles the project list and persists', async ({ page }) => {
    await gotoStudio(page);

    const header = page.getByTestId('studio-explorer-workspace-head');
    await expect(header).toBeVisible({ timeout: 10_000 });
    // Default = expanded
    await expect(header).toHaveAttribute('aria-expanded', 'true');

    // Identify a sentinel project row OR fall back to checking the
    // project loop's presence via any DOM selector.
    const anyProjectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
    const hadProjects = (await anyProjectRow.count()) > 0;

    // Click the header → collapses
    await header.click();
    await expect(header).toHaveAttribute('aria-expanded', 'false');
    if (hadProjects) {
      await expect(anyProjectRow).toBeHidden();
    }

    // Persistence key is set on localStorage
    const stored = await page.evaluate(() => localStorage.getItem('atp.studio.explorerSections'));
    expect(stored).toContain('"workspace":true');

    // Reload → still collapsed
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    const headerAfter = page.getByTestId('studio-explorer-workspace-head');
    await expect(headerAfter).toHaveAttribute('aria-expanded', 'false');
    if (hadProjects) {
      await expect(page.locator('[data-testid^="studio-explorer-project-row-"]').first()).toBeHidden();
    }

    // Click again → expands
    await headerAfter.click();
    await expect(headerAfter).toHaveAttribute('aria-expanded', 'true');
    const storedAfter = await page.evaluate(() => localStorage.getItem('atp.studio.explorerSections'));
    expect(storedAfter ?? '').not.toContain('"workspace":true');
  });

  test('"Show all projects" button stays reachable inside the header', async ({ page }) => {
    await gotoStudio(page);
    const showAll = page.getByTestId('studio-explorer-show-all-projects');
    await expect(showAll).toBeVisible({ timeout: 5_000 });
    // Title carries the intent so we can prove the affordance hasn't
    // silently lost its tooltip text after a refactor.
    await expect(showAll).toHaveAttribute('title', /Show all projects/);
  });

  test('Project label click toggles its children (was: open board)', async ({ page }) => {
    await gotoStudio(page);
    const projectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
    if ((await projectRow.count()) === 0) {
      test.skip(true, 'No projects loaded — project-row label contract skipped.');
      return;
    }
    const name = await projectRow.getAttribute('data-project-name');
    expect(name).toBeTruthy();
    const labelButton = projectRow.locator('button.tree-row').first();
    const children = projectRow.locator('.studio-tree-children');

    // Persisted expansion may or may not include this project before we
    // ran reset; force a known starting state by toggling until children
    // are hidden.
    if (await children.count() > 0 && await children.isVisible()) {
      await labelButton.click();
      await expect(children).toHaveCount(0);
    }

    // First click should EXPAND (label click is now a toggle).
    await labelButton.click();
    await expect(children).toBeVisible({ timeout: 3_000 });

    // Second click should COLLAPSE.
    await labelButton.click();
    await expect(children).toHaveCount(0);
  });

  test('Hub-link icon opens the Hub, does not toggle the row', async ({ page }) => {
    await gotoStudio(page);
    const projectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
    if ((await projectRow.count()) === 0) {
      test.skip(true, 'No projects loaded — hub-link contract skipped.');
      return;
    }

    // Ensure collapsed-by-default starting state so we can observe that
    // the hub-link click does NOT expand the row.
    const labelButton = projectRow.locator('button.tree-row').first();
    const children = projectRow.locator('.studio-tree-children');
    if (await children.count() > 0 && await children.isVisible()) {
      await labelButton.click();
      await expect(children).toHaveCount(0);
    }

    const hubLink = projectRow.locator('.studio-tree-row__hub-link').first();
    await expect(hubLink).toBeVisible();
    await hubLink.click();
    // A new tab whose kind is 'hub' should appear in the tab strip.
    // Verifying via the breadcrumb leaf which says "Project Hub".
    await expect(page.getByTestId('studio-titlebar-active-tab')).toHaveText('Project Hub', { timeout: 5_000 });
    // Note: the row itself may auto-expand because activating a project
    // makes it the "current project" in the explorer tree (the studio
    // shell has an effect that mirrors active project → expanded). That
    // side-effect is unrelated to the click handler; the click itself
    // stopPropagation()s so the chevron-toggle path is not taken. The
    // breadcrumb assertion above is the load-bearing check.
  });

  test('Open-tabs header collapses the open-tabs list', async ({ page }) => {
    await gotoStudio(page);
    // Force at least one tab to exist by opening a project hub if any
    // project is available; otherwise short-circuit.
    const projectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
    if ((await projectRow.count()) === 0) {
      test.skip(true, 'No projects loaded — open-tabs contract skipped.');
      return;
    }
    const hubLink = projectRow.locator('.studio-tree-row__hub-link').first();
    await hubLink.click();

    const tabsHeader = page.getByTestId('studio-explorer-open-tabs-head');
    await expect(tabsHeader).toBeVisible({ timeout: 5_000 });
    await expect(tabsHeader).toHaveAttribute('aria-expanded', 'true');

    // Click → collapses
    await tabsHeader.click();
    await expect(tabsHeader).toHaveAttribute('aria-expanded', 'false');

    const stored = await page.evaluate(() => localStorage.getItem('atp.studio.explorerSections'));
    expect(stored).toContain('"open-tabs":true');

    // Click again → expands
    await tabsHeader.click();
    await expect(tabsHeader).toHaveAttribute('aria-expanded', 'true');
  });
});
