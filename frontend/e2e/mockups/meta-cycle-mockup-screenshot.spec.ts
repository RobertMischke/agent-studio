import { test, expect } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * Captures screenshots of the orchestrator meta-cycle mockup so the task's
 * deliverable can be reviewed without standing up the dev frontend. The
 * mockup is a static HTML click-dummy under
 * `docs/mockups/orchestrator-meta-cycle/ui.html`; opening it via `file://`
 * is intentional - it does not depend on the backend being up.
 *
 * Output target: per-job `results/` so the screenshots survive past the
 * next test run (Playwright's `test-results/` is scratch).
 * See `docs/contracts/protocol-style.md`.
 */

const FRONTEND_DIR = process.cwd();
const REPO_ROOT = path.resolve(FRONTEND_DIR, '..');
const MOCKUP_PATH = path.join(REPO_ROOT, 'docs', 'mockups', 'orchestrator-meta-cycle', 'ui.html');
const MOCKUP_URL = 'file:///' + MOCKUP_PATH.replace(/\\/g, '/');

const JOB_RESULTS = String.raw`C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\3-progress\orchestrator-meta-cycle-self-monitor\results`;

test.use({ viewport: { width: 1280, height: 920 } });

test('meta-cycle mockup screenshots — overview, last cycle, configuration, banner states', async ({ page }) => {
  expect(fs.existsSync(MOCKUP_PATH)).toBe(true);
  fs.mkdirSync(JOB_RESULTS, { recursive: true });

  await page.goto(MOCKUP_URL);
  await expect(page.locator('header .brand')).toBeVisible();
  await expect(page.locator('#banner-pill')).toBeVisible();
  await page.screenshot({
    path: path.join(JOB_RESULTS, 'meta-cycle-mockup-01-overview.png'),
    fullPage: true,
  });

  await page.locator('.nav .tab[data-tab="last"]').click();
  await expect(page.locator('#tab-last')).toBeVisible();
  await page.screenshot({
    path: path.join(JOB_RESULTS, 'meta-cycle-mockup-02-last-cycle.png'),
    fullPage: true,
  });

  await page.locator('.nav .tab[data-tab="config"]').click();
  await expect(page.locator('#tab-config')).toBeVisible();
  await page.screenshot({
    path: path.join(JOB_RESULTS, 'meta-cycle-mockup-03-config.png'),
    fullPage: true,
  });

  await page.locator('.nav .tab[data-tab="history"]').click();
  await expect(page.locator('#tab-history')).toBeVisible();
  await page.screenshot({
    path: path.join(JOB_RESULTS, 'meta-cycle-mockup-04-history.png'),
    fullPage: true,
  });

  // Drive the banner through every state so the click-dummy renders one
  // screenshot per status pill: running / inspecting / paused / fix / escalated.
  await page.locator('.nav .tab[data-tab="overview"]').click();
  for (const state of ['running', 'inspecting', 'paused', 'fix', 'escalated']) {
    await page.evaluate((s) => (window as unknown as { setState: (s: string) => void }).setState(s), state);
    await page.waitForTimeout(120);
    await page.locator('#banner').screenshot({
      path: path.join(JOB_RESULTS, `meta-cycle-mockup-banner-${state}.png`),
    });
  }
});
