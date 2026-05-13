import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { listJobs } from './helpers/jobs';
import { api } from './helpers/api';

/** Job results folder for this regression's review-relevant evidence.
 *  Falls back to scratch `test-results/` when the orchestrator did not
 *  set JOB_RESULTS_DIR (i.e. local dev runs of the spec). */
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? 'test-results';

/**
 * Regression: the maximized "Agent log" modal must be horizontally and
 * vertically centered in the viewport. The bug was a left-aligned panel
 * because the native <dialog> rule relied on UA defaults that did not
 * survive the surrounding `<app-job-detail>` positioning context. The fix
 * pins the dialog to the viewport with `position: fixed; inset: 0;
 * margin: auto;` so it behaves like the rest of the app's overlays.
 *
 * Reuses an existing job that already has CLI output (so the "Maximize
 * log" button is rendered). The test is read-only; nothing is written.
 */

async function findJobWithOutput(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  for (const j of jobs) {
    try {
      const out = await api<unknown[]>(`/api/jobs/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`);
      if (Array.isArray(out) && out.length > 0) return { id: j.id, watchPath: j.watchPath };
    } catch { /* ignore */ }
  }
  return null;
}

async function openLogOverlay(page: Page): Promise<void> {
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 10_000 });
  await activityTab.click();

  const maximize = page.getByTestId('protocol-maximize-log');
  await expect(maximize).toBeVisible({ timeout: 10_000 });
  await maximize.click();

  const overlay = page.getByTestId('log-overlay');
  await expect(overlay).toBeVisible({ timeout: 5_000 });
}

/**
 * Asserts the modal centers horizontally in the given viewport. Allows a
 * small tolerance for sub-pixel rounding (the panel width is
 * `min(1400px, 96vw)`, so on a 1440-wide viewport the gutters should be
 * about 28px each).
 */
async function assertHorizontallyCentered(page: Page, viewportWidth: number) {
  const overlay = page.getByTestId('log-overlay');
  const box = await overlay.boundingBox();
  expect(box, 'log-overlay must have a bounding box').not.toBeNull();
  const left = box!.x;
  const right = viewportWidth - (box!.x + box!.width);
  const delta = Math.abs(left - right);
  // 2px slack covers sub-pixel browser rounding without letting a real
  // 100px+ left-shift slip through.
  expect(delta, `dialog left gutter (${left}) and right gutter (${right}) must match within 2px`).toBeLessThanOrEqual(2);
}

test.describe('Log overlay (maximized agent log) — centering', () => {
  test('is horizontally centered on a wide desktop viewport', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }
    await page.setViewportSize({ width: 1600, height: 1000 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    await openLogOverlay(page);
    await assertHorizontallyCentered(page, 1600);

    // The panel must also leave room on top and bottom rather than docking
    // to one edge; with height: 92vh and inset: 0 + margin: auto the top
    // gutter should equal the bottom gutter.
    const box = await page.getByTestId('log-overlay').boundingBox();
    const topGutter = box!.y;
    const bottomGutter = 1000 - (box!.y + box!.height);
    expect(Math.abs(topGutter - bottomGutter)).toBeLessThanOrEqual(2);

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'log-overlay-centered-wide.png'),
      fullPage: false
    });
  });

  test('is horizontally centered on a narrower desktop viewport', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }
    await page.setViewportSize({ width: 1200, height: 800 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    await openLogOverlay(page);
    await assertHorizontallyCentered(page, 1200);

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'log-overlay-centered-narrow.png'),
      fullPage: false
    });
  });
});
