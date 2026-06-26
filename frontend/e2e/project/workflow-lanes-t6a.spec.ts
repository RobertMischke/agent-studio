import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Nav-rebuild step 3 (T6a) — Workflow / Lanes page, stage 1.
 *
 * Acceptance: the Workflow rail renders the read-mostly transparency surface —
 * the lane list in board order, the relocated per-lane sort controls, and a
 * read-only view of what the platform does at each transition today
 * (auto-commit, attribution, gates, auto-push). The stage 2/3 Git work stays
 * a clearly-labelled placeholder, never a control, until the Git concept is
 * decided (docs/concepts/git-branching-integration-zielbild.md §7).
 *
 * Runs against the dedicated Playwright project so nothing is mutated; the
 * spec only navigates and asserts presence, then captures one real-backend
 * shot of the page.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'workflow-lanes-t6a');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'workflow-lanes-t6a');
})();

let projectSlug = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectSlug = slugFor(preferred.name);
});

test('Workflow rail renders lane list, transitions, and stage 2/3 placeholders', async ({ page }) => {
  await page.goto(`/#/projects/${projectSlug}/workflow`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  // 1. Lane list in board order — one row per sortable lane.
  const laneList = page.getByTestId('workflow-lane-list');
  await expect(laneList).toBeVisible();
  await expect(page.getByTestId('workflow-lane-3-progress')).toBeVisible();
  await expect(page.getByTestId('workflow-lane-4-auto-review')).toBeVisible();

  // 2. Relocated per-lane sort controls still mount here (T5b contract).
  await expect(page.getByTestId('project-detail-lane-sort')).toBeVisible();

  // 3. Read-only transition view — the four implemented facets, each with a
  //    live state pill.
  await expect(page.getByTestId('workflow-transitions')).toBeVisible();
  for (const key of ['auto-commit', 'attribution', 'gates', 'auto-push']) {
    await expect(page.getByTestId(`workflow-transition-${key}`)).toBeVisible();
    const state = page.getByTestId(`workflow-transition-state-${key}`);
    await expect(state).toBeVisible();
    await expect(state).not.toHaveText('…'); // settings resolved, not the loading dash
  }

  // 4. Stage 2/3 are labelled placeholders, not behaviour switches.
  const stage2 = page.getByTestId('workflow-stage2-placeholder');
  const stage3 = page.getByTestId('workflow-stage3-placeholder');
  await expect(stage2).toBeVisible();
  await expect(stage3).toBeVisible();
  await expect(stage2.locator('input, select, button')).toHaveCount(0);
  await expect(stage3.locator('input, select, button')).toHaveCount(0);

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, 'workflow-lanes-stage1--real.png'),
    fullPage: true,
  });
});
