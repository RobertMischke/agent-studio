import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * T5a (ASS-1737) nav-rebuild step 1 — before/after visual evidence.
 *
 * Step 1 only adds REACHABLE SHELLS to the target navigation; it moves no
 * content. This spec drives a real Chromium and screenshots the two surfaces
 * the step touches so a reviewer can compare old vs new:
 *
 *   - PROJECT level: the new Pipeline / Workflow / Prompts rows in the Config
 *     rail group, plus the generic placeholder panel one of them renders.
 *   - WORKSPACE level: the bottom-pinned Admin destination in the activity bar
 *     and its panel (CLI & Modelle / System).
 *
 * The interactions are best-effort (guarded by element presence) so the SAME
 * spec runs green against the pre-change stable stack ("before") and the
 * worktree under development ("after"). The strict behavioural assertions live
 * in the two unit specs; this layer is purely for the review screenshots.
 *
 * Output dir: T5A_SHOT_DIR (the job folder's results/.../before|after when set);
 * a local fallback keeps a stand-alone run useful.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.T5A_SHOT_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'nav-rebuild-t5a');
})();

let projectName = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function shot(page: Page, name: string) {
  return page.screenshot({ path: path.join(SCREENSHOT_DIR, name), fullPage: true });
}

async function openHub(page: Page) {
  await page.goto('/');
  const openBtn = page.getByTestId(`project-shell-open-${projectName}`);
  if (await openBtn.count()) {
    await openBtn.first().click();
  } else {
    await page.goto(`/#/projects/${slugFor(projectName)}/overview`);
  }
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
}

const rail = (page: Page, key: string) => page.getByTestId(`project-shell-rail-${key}`);

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

test('project rail: Pipeline / Workflow / Prompts shells in Config', async ({ page }) => {
  await openHub(page);
  await shot(page, '01-project-rail.png');

  // After-state only: the three new shells exist as top-level Config rows and
  // each renders the generic placeholder panel with a Step-2 hint.
  for (const key of ['pipeline', 'workflow', 'prompts']) {
    if (await rail(page, key).count()) {
      await rail(page, key).click();
      await expect(page.getByTestId(`project-shell-panel-${key}`)).toBeVisible();
      await shot(page, `02-project-${key}-placeholder.png`);
    }
  }
});

test('workspace: Admin destination (CLI & Modelle / System)', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('studio-activity-bar')).toBeVisible();
  await shot(page, '03-workspace-activity-bar.png');

  // After-state only: the bottom-pinned Admin button opens the admin panel.
  const adminBtn = page.getByTestId('studio-ab-admin');
  if (await adminBtn.count()) {
    await adminBtn.click();
    await expect(page.getByTestId('studio-admin-panel')).toBeVisible();
    await shot(page, '04-workspace-admin-panel.png');
  }
});
