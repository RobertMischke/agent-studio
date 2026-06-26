import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Browser/visual verification for the Project-Hub nav IA (ASS-1711).
 *
 * The unit spec (`project-shell.component.spec.ts`) covers the render path in
 * jsdom; this spec exercises the SAME interactions in a real Chromium against
 * a running app, and captures before/after screenshots as review evidence:
 *
 *   1. Default rail renders the four collapsible segments fully expanded.
 *   2. A main segment header collapses (its items leave the DOM) and re-expands.
 *   3. Context contains Architecture / Wiki / Agent Docs / Prompts and folds
 *      as a normal segment.
 *   4. The navigable "Settings" parent's twisty folds its sub-pages.
 *   5. The renamed "Agent Docs" leaf and Prompts point are present in Context.
 *
 * Screenshots land in NAV_IA_RESULTS_DIR (the orchestrator job folder's
 * results/ when set) so the visual evidence sits next to the protocol; a
 * local fallback keeps a stand-alone run useful.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.NAV_IA_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-hub-nav-ia');
})();

let projectName = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function shot(page: Page, name: string) {
  return page.screenshot({ path: path.join(SCREENSHOT_DIR, name), fullPage: true });
}

/** Open the project hub. Prefer the kanban "open project page" button; fall
 *  back to the deep-link hash route when the board tab is not on screen. */
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
const group = (page: Page, id: string) => page.getByTestId(`project-shell-group-${id}`);
const twisty = (page: Page, key: string) => page.getByTestId(`project-shell-twisty-${key}`);

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

test('default rail shows four collapsible segments with Agent Docs + Prompts in Context', async ({ page }) => {
  await openHub(page);

  for (const id of ['insight', 'quality', 'context', 'config']) {
    await expect(group(page, id), `segment ${id}`).toBeVisible();
    await expect(group(page, id)).toHaveAttribute('aria-expanded', 'true');
  }

  await expect(rail(page, 'architecture')).toBeVisible();
  await expect(rail(page, 'wiki')).toBeVisible();
  await expect(rail(page, 'steering')).toContainText('Agent Docs');
  await expect(rail(page, 'prompts')).toContainText('Prompts');
  await expect(rail(page, 'runtime-prompts')).toHaveCount(0);

  // Settings is a navigable parent with a twisty + two sub-pages.
  await expect(twisty(page, 'settings')).toBeVisible();
  await expect(rail(page, 'settings-defaults')).toContainText('Workspace Defaults');
  await expect(rail(page, 'settings-overrides')).toContainText('Project Overrides');

  await shot(page, '00-rail-default-expanded.png');
});

test('collapsing a main segment hides its items and re-expanding restores them', async ({ page }) => {
  await openHub(page);

  await expect(rail(page, 'security')).toBeVisible();
  await group(page, 'quality').click();
  await expect(group(page, 'quality')).toHaveAttribute('aria-expanded', 'false');
  await expect(rail(page, 'security')).toHaveCount(0);
  await expect(rail(page, 'test-quality')).toHaveCount(0);
  await shot(page, '01-segment-quality-collapsed.png');

  await group(page, 'quality').click();
  await expect(group(page, 'quality')).toHaveAttribute('aria-expanded', 'true');
  await expect(rail(page, 'security')).toBeVisible();
  await shot(page, '02-segment-quality-reexpanded.png');
});

test('the Context segment folds and unfolds its documentation rows', async ({ page }) => {
  await openHub(page);

  await expect(rail(page, 'architecture')).toBeVisible();
  await expect(rail(page, 'prompts')).toBeVisible();

  await group(page, 'context').click();
  await expect(group(page, 'context')).toHaveAttribute('aria-expanded', 'false');
  await expect(rail(page, 'architecture')).toHaveCount(0);
  await expect(rail(page, 'wiki')).toHaveCount(0);
  await expect(rail(page, 'steering')).toHaveCount(0);
  await expect(rail(page, 'prompts')).toHaveCount(0);
  await shot(page, '03-context-collapsed.png');

  await group(page, 'context').click();
  await expect(group(page, 'context')).toHaveAttribute('aria-expanded', 'true');
  await expect(rail(page, 'architecture')).toBeVisible();
  await expect(rail(page, 'prompts')).toBeVisible();
  await shot(page, '04-context-reexpanded.png');
});

test('the Settings twisty folds and unfolds its grouped sub-pages', async ({ page }) => {
  await openHub(page);

  await expect(rail(page, 'settings-defaults')).toBeVisible();
  await twisty(page, 'settings').click();
  await expect(rail(page, 'settings-defaults')).toHaveCount(0);
  await expect(rail(page, 'settings-overrides')).toHaveCount(0);
  await expect(rail(page, 'settings')).toBeVisible();
  await shot(page, '05-settings-collapsed.png');

  await twisty(page, 'settings').click();
  await expect(rail(page, 'settings-defaults')).toBeVisible();
  await shot(page, '06-settings-reexpanded.png');
});
