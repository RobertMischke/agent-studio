import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Verifies the Workspace dropdown in the per-project Settings rail.
 *
 * The dropdown lists every configured watch path with the current
 * project pre-selected, the Save button is gated on a different
 * selection, and the click invokes a confirmation dialog before
 * issuing the change-project calls. We exercise the picker and the
 * cancel branch only; the destructive Save path that would relocate
 * every job is excluded so the spec is non-billable and idempotent.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-settings-workspace-dropdown');
})();

let projectName = '';
let projectPath = '';
let otherPath = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThanOrEqual(2);
  const preferred = paths.find(p => /agent.?task|software.?studio/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
  projectPath = preferred.path;
  const alt = paths.find(p => p.path !== projectPath)!;
  otherPath = alt.path;
});

test('workspace dropdown lists every watch path with the current one selected', async ({ page }) => {
  const slug = slugFor(projectName);
  await page.goto(`/#/projects/${slug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-detail-workspace')).toBeVisible();

  const select = page.getByTestId('project-detail-workspace-select');
  await expect(select).toBeVisible();
  await expect(select).toHaveValue(projectPath);

  const options = await select.locator('option').allTextContents();
  const paths = await api<WatchPath[]>('/api/watch-paths');
  for (const wp of paths) {
    expect(options).toContain(wp.name);
  }

  await expect(page.getByTestId('project-detail-workspace-save')).toBeDisabled();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-default-selected.png'), fullPage: true });
});

test('selecting a different workspace enables Save and confirm dialog opens', async ({ page }) => {
  const slug = slugFor(projectName);
  await page.goto(`/#/projects/${slug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-detail-workspace')).toBeVisible();

  const select = page.getByTestId('project-detail-workspace-select');
  await select.selectOption(otherPath);

  const save = page.getByTestId('project-detail-workspace-save');
  await expect(save).toBeEnabled();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-different-selected.png'), fullPage: true });

  // Open the confirm and cancel so the test stays non-destructive.
  // The dialog title is owned by the app's confirm-dialog component.
  await save.click();
  const dialog = page.getByTestId('confirm-dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('Move project to another workspace?');

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-confirm-dialog.png'), fullPage: true });

  await page.getByTestId('confirm-dialog-cancel').click();
  await expect(dialog).not.toBeVisible();
});
