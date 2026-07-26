import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

/**
 * ASS-658/ASS-597: a project node in the Workspaces Explorer exposes exactly
 * five project-scoped links: "Board" (the kanban), "Deck", "Wiki",
 * "Workbenches", and "Epics" (overview). The retired per-lane children (active /
 * human review / archive) stay gone, and Epics opens scoped to the
 * clicked project rather than the global rollup.
 *
 * Runs against the live backend's project list; short-circuits when the
 * configured board has no projects.
 */

async function gotoStudio(page: Page): Promise<void> {
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }),
  }));
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
  if (!await row.isVisible({ timeout: 10_000 }).catch(() => false)) return null;
  const name = (await row.getAttribute('data-project-name')) ?? '';
  const label = row.locator('button.tree-row').first();
  const children = row.locator('.studio-tree-children');
  if (!(await children.count()) || !(await children.isVisible())) {
    await label.click();
    await expect(children).toBeVisible({ timeout: 3_000 });
  }
  return { row, name };
}

const DECK_NAMING_SCREENSHOTS = path.resolve(
  __dirname,
  '..',
  '..',
  'playwright-screenshots',
  'deck-naming',
);
const LEGACY_DECK_NAME = ['Project', 'Hub'].join(' ');

test.describe('Explorer · project links to Board / Deck / Wiki / Epics', () => {
  test('Deck naming is visible in the tree and opened surface in both themes', async ({ page, devBackend }, testInfo) => {
    expect(devBackend.port).toBeGreaterThan(0);
    mkdirSync(DECK_NAMING_SCREENSHOTS, { recursive: true });

    for (const theme of ['light', 'dark'] satisfies Theme[]) {
      await gotoStudio(page);
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);

      const expanded = await expandFirstProject(page);
      expect(expanded, 'The dev-backend fixture must expose a project').not.toBeNull();
      const { name } = expanded!;
      const deckRow = page.getByTestId(`studio-explorer-project-deck-${name}`);

      await expect(deckRow).toContainText('Deck');
      await expect(deckRow.getByRole('button', { name: `Deck, ${name}` })).toBeVisible();

      const deckShortcut = page.locator('button[aria-label="Open Deck"]').first();
      await expect(deckShortcut).toHaveAttribute('aria-label', 'Open Deck');

      await deckRow.getByRole('button', { name: `Deck, ${name}` }).click();
      await expect(page.getByRole('tab', { name: / · Deck$/ }))
        .toHaveAttribute('aria-selected', 'true');
      await expect(page.getByTestId('deck-sidebar-header')).toContainText('Deck');
      await page.getByTestId('project-shell').hover({ position: { x: 400, y: 300 } });

      await expect(page.locator('body')).not.toContainText(LEGACY_DECK_NAME);
      await expect(page.locator(
        `[aria-label*="${LEGACY_DECK_NAME}"], [title*="${LEGACY_DECK_NAME}"]`,
      )).toHaveCount(0);

      const screenshotPath = path.join(DECK_NAMING_SCREENSHOTS, `deck-tree-open-${theme}.png`);
      await page.screenshot({ path: screenshotPath, fullPage: true });
      await testInfo.attach(`Deck tree and opened surface (${theme})`, {
        path: screenshotPath,
        contentType: 'image/png',
      });
    }
  });

  test('expanded project shows exactly the five project links, no lane rows', async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — project-link contract skipped.');
      return;
    }
    const { row, name } = expanded;
    const children = row.locator('.studio-tree-children');

    // Exactly five child rows.
    const rows = children.locator('.tree-row');
    await expect(rows).toHaveCount(5);

    // Each is addressable by its stable testid.
    await expect(page.getByTestId(`studio-explorer-project-board-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-deck-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-wiki-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-workbenches-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-epics-${name}`)).toBeVisible();

    // The retired per-lane labels must not appear under the project node.
    const childText = (await children.innerText()).toLowerCase();
    for (const gone of ['active', 'human review', 'archive', 'project view']) {
      expect(childText).not.toContain(gone);
    }
    expect(childText).toContain('board');
    expect(childText).toContain('deck');
    expect(childText).not.toContain('backlog');
    expect(childText).toContain('epics');
  });

  test('"Board" opens the project kanban', async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — Board-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-board-${expanded.name}`).click();
    await expect(page.getByTestId(`studio-tab-board:${expanded.name}`))
      .toHaveAttribute('aria-selected', 'true', { timeout: 5_000 });

    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText(expanded.name);
    await expect(page.getByTestId('studio-titlebar-workspace')).toHaveCount(0);
    await expect(page.getByTestId('studio-titlebar-active-tab')).toHaveCount(0);
    await expect(page.getByTestId('studio-titlebar-crumbs')).not.toContainText('Board');
  });

  test('"Deck" opens the Deck', async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded - Deck-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-deck-${expanded.name}`)
      .getByRole('button', { name: `Deck, ${expanded.name}` })
      .click();
    await expect(page.getByRole('tab', { name: / · Deck$/ }))
      .toHaveAttribute('aria-selected', 'true', { timeout: 5_000 });
  });

});
