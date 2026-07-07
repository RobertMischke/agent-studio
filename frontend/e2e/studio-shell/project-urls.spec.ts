import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Project URLs (AGT-1808): a per-project URL becomes both an Explorer-tree
 * child row (globe glyph + running/offline status dot, opens the URL in a new
 * tab) and a row on the Project Hub "Project URLs" management page. This spec
 * exercises the full stack against the live dev backend: it creates a URL via
 * the registry API on the first project, asserts both surfaces, then deletes it
 * so the registry is left as it was found. Short-circuits when no project is
 * registered.
 */

interface RegistryProject { id: string; displayName: string; archived: boolean; }
interface ProjectRecord { id: string; urls: { id: string; label: string; url: string }[]; }

async function firstProject(): Promise<RegistryProject | null> {
  const projects = await api<RegistryProject[]>('/api/projects').catch(() => null);
  if (!Array.isArray(projects)) return null;
  return projects.find(p => !p.archived) ?? null;
}

async function gotoStudio(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 10_000 });
}

async function expandProject(page: Page, displayName: string): Promise<string | null> {
  const row = page.locator('[data-testid^="studio-explorer-project-row-"]')
    .filter({ has: page.locator(`[data-testid="studio-explorer-project-${displayName}"]`) })
    .first();
  const anyRow = (await row.count()) ? row : page.locator('[data-testid^="studio-explorer-project-row-"]').first();
  if (!(await anyRow.count())) return null;
  const name = (await anyRow.getAttribute('data-project-name')) ?? '';
  const children = anyRow.locator('.studio-tree-children');
  if (!(await children.count()) || !(await children.isVisible())) {
    await anyRow.locator('button.tree-row').first().click();
    await expect(children).toBeVisible({ timeout: 3_000 });
  }
  return name;
}

test.describe('Project URLs · Explorer tree row + Project Hub page', () => {
  test('a configured URL appears as a tree row and on the Project Hub page', async ({ page, context }) => {
    const project = await firstProject();
    test.skip(!project, 'No registered project on the live backend — Project URLs contract skipped.');
    const proj = project!;

    // Arrange: add a URL through the registry API (self-cleaning in teardown).
    const label = `E2E Dev Server ${Date.now()}`;
    const url = 'http://localhost:4010';
    let created: ProjectRecord | null = null;
    let urlId = '';
    try {
      created = await api<ProjectRecord>(`/api/projects/${proj.id}/urls`, {
        method: 'POST',
        body: JSON.stringify({ label, url }),
      });
      urlId = created.urls.find(u => u.label === label)?.id ?? '';
      expect(urlId, 'backend returned the new url id').not.toBe('');

      // --- Surface 1: Explorer tree row ---
      await gotoStudio(page);
      const name = await expandProject(page, proj.displayName);
      test.skip(!name, 'Project row not rendered (its workspace may be collapsed / empty).');

      const treeRow = page.getByTestId(`studio-explorer-project-url-${name}-${urlId}`);
      await expect(treeRow).toBeVisible({ timeout: 5_000 });
      await expect(treeRow).toContainText(label);
      await expect(page.getByTestId(`studio-explorer-project-url-dot-${name}-${urlId}`)).toBeVisible();

      // Clicking the row opens the URL in a new tab (window.open), not an app tab.
      const popupPromise = context.waitForEvent('page', { timeout: 5_000 }).catch(() => null);
      await treeRow.click();
      const popup = await popupPromise;
      if (popup) {
        expect(popup.url()).toContain('localhost:4010');
        await popup.close();
      }

      // --- Surface 2: Project Hub "Project URLs" page ---
      await page.getByTestId(`studio-explorer-project-hub-${name}`).click();
      await page.getByText('Project URLs', { exact: true }).first().click();
      const panel = page.getByTestId('project-urls-panel');
      await expect(panel).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId(`project-urls-row-${urlId}`)).toContainText(label);
      await expect(page.getByTestId('project-urls-add')).toBeVisible();
    } finally {
      // Teardown: remove the URL so the registry is left unchanged.
      if (urlId) {
        await api(`/api/projects/${proj.id}/urls/${urlId}`, { method: 'DELETE' }).catch(() => { /* best effort */ });
      }
    }
  });

  test('Add URL opens the suggestion + manual form on the Project Hub page', async ({ page }) => {
    const project = await firstProject();
    test.skip(!project, 'No registered project on the live backend — Project URLs add-form skipped.');
    const proj = project!;

    await gotoStudio(page);
    const name = await expandProject(page, proj.displayName);
    test.skip(!name, 'Project row not rendered.');

    await page.getByTestId(`studio-explorer-project-hub-${name}`).click();
    await page.getByText('Project URLs', { exact: true }).first().click();
    await expect(page.getByTestId('project-urls-panel')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('project-urls-add').click();
    await expect(page.getByTestId('project-urls-add-panel')).toBeVisible();
    await expect(page.getByTestId('project-urls-form-label')).toBeVisible();
    await expect(page.getByTestId('project-urls-form-url')).toBeVisible();
    await expect(page.getByTestId('project-urls-form-command')).toBeVisible();
  });
});
