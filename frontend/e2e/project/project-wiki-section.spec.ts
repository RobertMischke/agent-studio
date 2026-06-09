import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Project-level Wiki / Docs surface. Verifies that the docs/ tree renders
 * grouped by folder, that selecting a document opens its rendered Markdown
 * in the viewer, and that the filter narrows the tree. Navigation uses the
 * deep-link hash contract (`#/projects/<slug>/wiki`) so the spec does not
 * depend on the kanban landing-page open button.
 *
 * Screenshots land in the orchestrator job results dir when
 * PROJECT_WIKI_RESULTS_DIR is set; otherwise a sibling of the spec so a
 * stand-alone run stays useful.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_WIKI_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-wiki-section');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.describe('Project detail - Wiki / Docs section', () => {
  let projectName = '';

  test.beforeAll(async () => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);
    // Prefer the agent-taskboard repo: it has a populated docs/ tree.
    const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
    projectName = preferred.name;
  });

  test('Wiki rail mounts the docs tree and opens a document', async ({ page }) => {
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);

    const panel = page.getByTestId('project-shell-panel-wiki');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await expect(panel).toHaveAttribute('data-rail-key', 'wiki');

    const section = panel.getByTestId('project-wiki-section');
    await expect(section).toBeVisible({ timeout: 10_000 });

    // The tree renders with at least one document button.
    const tree = page.getByTestId('project-wiki-tree');
    await expect(tree).toBeVisible();
    const firstFile = tree.locator('[data-testid^="project-wiki-file-"]').first();
    await expect(firstFile).toBeVisible({ timeout: 10_000 });

    // Count badge reflects a non-empty tree.
    await expect(page.getByTestId('project-wiki-count')).toContainText(/\d+ docs/);

    // Viewer starts in its empty state until a document is picked.
    await expect(page.getByTestId('project-wiki-viewer-empty')).toBeVisible();

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-wiki-tree.png'), fullPage: true });

    // Open the first document and confirm rendered Markdown shows up.
    await firstFile.click();
    await expect(page.getByTestId('project-wiki-viewer-path')).toBeVisible({ timeout: 10_000 });
    const viewer = page.getByTestId('project-wiki-viewer');
    await expect(viewer).toBeVisible();
    await expect(viewer.locator('h1, h2, h3').first()).toBeVisible({ timeout: 10_000 });

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-wiki-document.png'), fullPage: true });

    // Close returns to the empty state.
    await page.getByTestId('project-wiki-close').click();
    await expect(page.getByTestId('project-wiki-viewer-empty')).toBeVisible();
  });

  test('Filter narrows the document tree', async ({ page }) => {
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });

    const tree = page.getByTestId('project-wiki-tree');
    const before = await tree.locator('[data-testid^="project-wiki-file-"]').count();
    expect(before).toBeGreaterThan(0);

    // A needle that cannot match any path collapses the tree to the
    // no-match state without crashing.
    await page.getByTestId('project-wiki-filter').fill('zzz-no-such-doc-zzz');
    await expect(page.getByTestId('project-wiki-no-match')).toBeVisible({ timeout: 5_000 });
    await expect(tree.locator('[data-testid^="project-wiki-file-"]')).toHaveCount(0);

    // Clearing the filter restores the full tree.
    await page.getByTestId('project-wiki-filter').fill('');
    await expect(tree.locator('[data-testid^="project-wiki-file-"]').first()).toBeVisible({ timeout: 5_000 });
  });

  test('Unknown project surfaces the backend 404 without crashing', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('body')).toBeVisible();

    // The wiki overview endpoint answers 404 for an unknown project; the
    // surface relies on this rather than blank-screening the user.
    const res = await page.request.get('/api/projects/__wiki-no-such-project__/wiki');
    expect(res.status()).toBe(404);
  });
});
