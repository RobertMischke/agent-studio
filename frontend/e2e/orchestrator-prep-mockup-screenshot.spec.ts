import { test, expect } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * ADR-0026: captures screenshots of the orchestrator-prep + autonomy-scale
 * mockup so the task's deliverable can be reviewed without standing up the
 * dev frontend. The mockup is a static HTML click-dummy under
 * `docs/mockups/orchestrator-prep-and-autonomy/ui.html`.
 *
 * Output target: per-job `results/` so the screenshots survive past the
 * next test run (Playwright's `test-results/` is scratch).
 */

const FRONTEND_DIR = process.cwd();
const REPO_ROOT = path.resolve(FRONTEND_DIR, '..');
const MOCKUP_PATH = path.join(REPO_ROOT, 'docs', 'mockups', 'orchestrator-prep-and-autonomy', 'ui.html');
const MOCKUP_URL = 'file:///' + MOCKUP_PATH.replace(/\\/g, '/');

const JOB_RESULTS = String.raw`C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\3-progress\orchestrator-prep-lane-and-autonomy-scale\results`;

test.use({ viewport: { width: 1440, height: 900 } });

test('orchestrator-prep mockup: low-autonomy and fully-auto board states', async ({ page }) => {
  expect(fs.existsSync(MOCKUP_PATH)).toBe(true);
  fs.mkdirSync(JOB_RESULTS, { recursive: true });

  await page.goto(MOCKUP_URL);
  await expect(page.locator('header .brand')).toBeVisible();
  await expect(page.locator('#autonomy')).toBeVisible();

  // Default landing: scenario = low autonomy (level 1). The bounce lane
  // (1b-needs-human-review) is visible because the orchestrator is
  // bouncing borderline tasks for human clarification.
  await expect(page.locator('[data-lane="needs-clar"]')).toBeVisible();
  await page.screenshot({
    path: path.join(JOB_RESULTS, 'orchestrator-prep-mockup-01-low-autonomy.png'),
    fullPage: true,
  });

  // Walk to fully-auto via the slider. The bounce lane hides; the queue
  // refills; the orchestrator-prep cards switch to "will accept on cap exit".
  await page.locator('#scenario-high').click();
  await expect(page.locator('[data-lane="needs-clar"]')).toBeHidden();
  await expect(page.locator('#level-title')).toContainText('fully auto');
  await page.screenshot({
    path: path.join(JOB_RESULTS, 'orchestrator-prep-mockup-02-fully-auto.png'),
    fullPage: true,
  });

  // Manual (level 0): orchestrator-prep dimmed, queue at 0. Verify the
  // header strip + queue counters render the manual state.
  await page.locator('#autonomy').evaluate((el: HTMLInputElement) => {
    el.value = '0';
    el.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await expect(page.locator('#level-title')).toContainText('manual');
  await page.screenshot({
    path: path.join(JOB_RESULTS, 'orchestrator-prep-mockup-03-manual.png'),
    fullPage: true,
  });

  // Crop just the header strip (slider + level card) for a focused screenshot
  // the user can inline in a write-up.
  await page.locator('#scenario-low').click();
  const stripShot = await page.locator('.autonomy-strip').screenshot({
    path: path.join(JOB_RESULTS, 'orchestrator-prep-mockup-04-slider-strip.png'),
  });
  expect(stripShot.length).toBeGreaterThan(0);
});
