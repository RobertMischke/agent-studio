import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Verifies the per-project Settings rail renders the real
 * <app-project-settings-panel> (not the shell placeholder) when
 * deep-linked, mirrors the global Workspace-settings home, and surfaces
 * the inherited global defaults read-only with working deep-links into
 * the matching global Workspace-settings sections.
 *
 * The default-agent and usage-caps cards have no per-project override
 * backend, so they are read-only; we assert the "Inherited" badges and
 * that the "Open Workspace settings" / "Manage usage caps" links open
 * the global overlay on the right section. The embedded project-detail
 * overrides (workspace dropdown) must still render below. All branches
 * are read-only, so the spec is non-billable and idempotent.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-settings-panel');
})();

let projectName = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThanOrEqual(1);
  const preferred = paths.find(p => /agent.?task|software.?studio/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

test('settings rail renders the real panel mirroring the global defaults', async ({ page }) => {
  const slug = slugFor(projectName);
  await page.goto(`/#/projects/${slug}/settings`);

  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  // The shell mounts the real panel as the settings rail content.
  await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible();
  const panel = page.getByTestId('project-settings-panel');
  await expect(panel).toBeVisible();

  // Header mirrors the global home: title + "this project" scope chip.
  await expect(panel.getByTestId('project-settings-title')).toHaveText('Settings');
  await expect(panel.getByTestId('project-settings-desc')).toBeVisible();

  // Default-agent card: inherited, read-only agent chip + deep-link.
  const agentCard = panel.getByTestId('project-settings-default-agent');
  await expect(agentCard).toBeVisible();
  await expect(panel.getByTestId('project-settings-default-agent-inherited')).toHaveText('Inherited');
  await expect(panel.getByTestId('project-settings-default-agent-chip')).toBeVisible();
  await expect(panel.getByTestId('project-settings-open-workspace')).toBeVisible();

  // Usage-caps card: inherited, read-only summary + deep-link.
  const capsCard = panel.getByTestId('project-settings-usage-caps');
  await expect(capsCard).toBeVisible();
  await expect(panel.getByTestId('project-settings-usage-caps-inherited')).toHaveText('Inherited');
  await expect(panel.getByTestId('project-settings-open-caps')).toBeVisible();

  // Project overrides still render below (embedded project-detail).
  await expect(page.getByTestId('project-detail-workspace')).toBeVisible();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-settings-panel.png'), fullPage: true });
});

test('default-agent link opens the global Workspace-settings home', async ({ page }) => {
  const slug = slugFor(projectName);
  await page.goto(`/#/projects/${slug}/settings`);
  await expect(page.getByTestId('project-settings-panel')).toBeVisible({ timeout: 10_000 });

  await page.getByTestId('project-settings-open-workspace').click();

  await expect(page.getByTestId('workspace-settings-overlay')).toBeVisible();
  await expect(page.getByTestId('workspace-settings-overview')).toBeVisible();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-workspace-home.png'), fullPage: true });

  await page.getByTestId('workspace-settings-close').click();
  await expect(page.getByTestId('workspace-settings-overlay')).not.toBeVisible();
});

test('usage-caps link opens the global usage-caps section', async ({ page }) => {
  const slug = slugFor(projectName);
  await page.goto(`/#/projects/${slug}/settings`);
  await expect(page.getByTestId('project-settings-panel')).toBeVisible({ timeout: 10_000 });

  await page.getByTestId('project-settings-open-caps').click();

  await expect(page.getByTestId('workspace-settings-overlay')).toBeVisible();
  await expect(page.getByTestId('cli-admin-panel')).toBeVisible();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-usage-caps.png'), fullPage: true });

  await page.getByTestId('workspace-settings-close').click();
  await expect(page.getByTestId('workspace-settings-overlay')).not.toBeVisible();
});
