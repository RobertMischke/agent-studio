import { test, expect } from '@playwright/test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

// Renders the docs/mockups/task-progress-tracking/ui.html click-dummy and
// captures screenshots of the four UI states the mockup README claims.
// Output goes to the job folder (see RESULTS_DIR below) so screenshots
// survive past Playwright's `test-results/` scratch wipe.

const REPO_ROOT = path.resolve(__dirname, '..', '..', '..');
const MOCKUP_URL = pathToFileURL(path.join(REPO_ROOT, 'docs', 'mockups', 'task-progress-tracking', 'ui.html')).toString();
const RESULTS_DIR = path.resolve(
  REPO_ROOT,
  '..', '..',
  'agent-taskboard-workspace', 'projects', 'agent-taskboard',
  '3-progress', 'task-progress-tracking', 'results'
);

test.beforeAll(() => {
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
});

test.describe('task-progress-tracking mockup', () => {
  test('captures the four key states', async ({ page }) => {
    await page.setViewportSize({ width: 1180, height: 940 });
    await page.goto(MOCKUP_URL);

    // 1. Initial paint - empty plan strip, claude badge.
    await page.locator('#cli-select').waitFor();
    await page.screenshot({ path: path.join(RESULTS_DIR, '01-initial-empty.png'), fullPage: true });

    // 2. Mid-run with the soft-estimate band visible: play until items 1 and 2
    //    are done and item 3 is active with a few sub-actions accumulated.
    await page.click('#btn-play');
    await page.waitForFunction(() => {
      const items = Array.from(document.querySelectorAll('.item'));
      const doneCount = items.filter(n => n.classList.contains('done')).length;
      const active = items.find(n => n.classList.contains('active'));
      const ticks = active ? active.querySelectorAll('.tick.in').length : 0;
      return doneCount >= 2 && ticks >= 2;
    }, null, { timeout: 25000 });
    await page.click('#btn-pause');
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(RESULTS_DIR, '02-mid-run-with-soft-band.png'), fullPage: true });

    // 3. Run to completion, then expand item 1 to show its sub-action list.
    await page.click('#btn-play');
    await page.waitForFunction(
      () => /Run finished/.test(document.getElementById('btn-play')!.textContent || ''),
      null,
      { timeout: 25000 }
    );
    await page.locator('.item.done').first().click();
    await page.waitForTimeout(200);
    await page.screenshot({ path: path.join(RESULTS_DIR, '03-completed-expanded.png'), fullPage: true });

    // 4. Copilot variant: heuristic badge, soft-estimate band suppressed.
    await page.selectOption('#cli-select', 'copilot');
    await page.click('#btn-play');
    await page.waitForFunction(() => {
      const items = Array.from(document.querySelectorAll('.item'));
      return items.filter(n => n.classList.contains('done')).length >= 2;
    }, null, { timeout: 25000 });
    await page.click('#btn-pause');
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(RESULTS_DIR, '04-copilot-heuristic.png'), fullPage: true });

    // 5. No-plan CLI: empty-state copy.
    await page.selectOption('#cli-select', 'none');
    await page.click('#btn-play');
    await page.waitForTimeout(1500);
    await page.screenshot({ path: path.join(RESULTS_DIR, '05-no-plan-tracked.png'), fullPage: true });

    // Sanity: the badge text must reflect the source the README claims.
    await page.selectOption('#cli-select', 'codex');
    await expect(page.locator('#source-badge')).toHaveText(/codex \/ update_plan/);
  });
});
