import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'wiki-source-branch')
  : path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'wiki-source-branch');

const source = {
  mode: 'branch', branch: 'origin/develop', commit: 'abcdef1234567890',
  shortCommit: 'abcdef12', writable: false, error: null,
};

const tree = {
  projectName: 'Demo', baseDir: 'docs', exists: true, source,
  root: [{
    name: 'guides', title: 'Guides', relPath: 'guides', type: 'folder', metadata: null, children: [{
      name: 'operator.md', title: 'Operator guide', relPath: 'guides/operator.md',
      type: 'md', children: [], metadata: null, immutable: false,
    }],
  }],
};

const pulse = {
  projectName: 'Demo', baseDir: 'docs', exists: true, generatedAtUtc: '2026-07-12T03:00:00Z',
  feed: { available: true, reason: null, items: [] },
  inbox: { available: true, reason: null, count: 0, items: [] },
  drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [], counts: { fresh: 1, aging: 0, stale: 0, graded: 1 } },
  critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  warnings: { available: true, reason: null, count: 0, items: [] },
  activity: { available: true, reason: null, runs: [], collector: null, curator: null },
};

test('project-wide branch source is visible and read-only in both themes', async ({ page }) => {
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  let settingsUpdate: Record<string, unknown> | null = null;
  await page.route('**/api/watch-paths', route => route.fulfill({ json: [{ name: 'Demo', path: '/tmp/demo/jobs', rootPath: '/tmp/demo' }] }));
  await page.route('**/api/workspaces**', route => route.fulfill({ json: [{
    id: 'ws-default', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-07-12T00:00:00Z', projects: [{
      sourceType: 'local-folder', id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM',
      workspaceId: 'ws-default', color: null, cliDefault: null, modelDefault: null,
      sortOrder: 0, storageLocation: '/tmp/demo/jobs', urls: [], archived: false,
      createdAt: '2026-07-12T00:00:00Z', wikiSourceBranch: 'origin/develop',
    }],
  }] }));
  await page.route('**/api/git/inventory**', route => route.fulfill({ json: {
    isRepo: true, currentBranch: 'main', branches: [{ name: 'develop', upstream: 'origin/develop' }],
  } }));
  await page.route('**/api/projects/PROJ-001', async route => {
    settingsUpdate = route.request().postDataJSON();
    await route.fulfill({ json: { wikiSourceBranch: null } });
  });
  await page.route('**/api/projects/Demo/wiki/tree', route => route.fulfill({ json: tree }));
  await page.route('**/api/projects/Demo/wiki/pulse**', route => route.fulfill({ json: pulse }));
  await page.route('**/api/cli/maintenance-model', route => route.fulfill({ json: { cliType: 'claude', model: null, thinkingLevel: null } }));
  await page.route('**/wiki/grading/status**', route => route.fulfill({ json: { status: null } }));

  await page.goto('/#/projects/demo/wiki');
  await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-wiki-source')).toContainText('origin/develop @ abcdef12');
  await expect(page.getByTestId('project-wiki-new-page')).toBeDisabled();
  await expect(page.getByTestId('project-wiki-new-folder')).toBeDisabled();
  await expect(page.getByText('Unexpected application error')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    await page.getByTestId('project-wiki-header').screenshot({
      path: path.join(RESULTS_DIR, `wiki-branch-source--${theme}--real-app.png`),
    });
  }

  await page.goto('/#/projects/demo/settings');
  const select = page.getByTestId('project-settings-wiki-source-select');
  await expect(select).toBeVisible({ timeout: 10_000 });
  await expect(select).toHaveValue('origin/develop');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('project-settings-wiki-source').screenshot({
      path: path.join(RESULTS_DIR, `wiki-source-setting--${theme}--real-app.png`),
    });
  }
  await select.selectOption('');
  await expect.poll(() => settingsUpdate).toEqual({ clearWikiSourceBranch: true });
  await expect(select).toHaveValue('');
});
