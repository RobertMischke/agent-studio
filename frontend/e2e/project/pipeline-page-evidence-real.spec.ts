import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * T4a evidence (--real) — the reworked project-level Pipeline page rendered
 * against the LIVE backend with NO route mocks. The catalogue / overrides /
 * cost all come from the real stack, so this shot is labelled --real and
 * proves the redesigned panel renders with authentic data, not just a fixture.
 * Pure reads + a screenshot; it never writes, so it is safe against the
 * shared stack.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-page');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-page');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

let projectSlug = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectSlug = slugFor(preferred.name);
});

test('pipeline page (real): reworked panel renders against the live backend', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 2400 });

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  // The core step is always present in the real catalogue (live id: core-agent-run).
  await expect(page.getByTestId('pipeline-step-row-core-agent-run')).toBeVisible();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-full--real.png'), fullPage: true });
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-section--real.png') });
});
