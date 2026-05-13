import { test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const OUT_DIR = process.env.SHOT_OUT_DIR
  ?? 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/add-loading-state-to-archive-all-button/results';

test('shots: archive-all idle + loading', async ({ page }) => {
  fs.mkdirSync(OUT_DIR, { recursive: true });

  await page.goto('/');
  const overlay = page.locator('.overlay--error');
  if (await overlay.isVisible({ timeout: 500 }).catch(() => false)) {
    await overlay.click({ force: true }).catch(() => {});
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }

  // Hold the move POSTs so the loading state is observable.
  await page.route('**/api/jobs/*/move*', async (route, request) => {
    if (request.method() !== 'POST') return route.continue();
    await new Promise(r => setTimeout(r, 6000));
    return route.continue();
  });

  const btn = page.getByTestId('archive-all-btn').first();
  await btn.waitFor({ state: 'visible', timeout: 10_000 });
  await btn.scrollIntoViewIfNeeded();

  // Crop to a tight rect around the button + lane header.
  const lane = page.locator('[data-testid="lane-6-completed"]').first();
  const box = await lane.boundingBox();
  const clip = box
    ? {
        x: Math.max(0, box.x - 4),
        y: Math.max(0, box.y - 4),
        width: Math.min(600, box.width + 8),
        height: 120
      }
    : undefined;

  await page.screenshot({ path: path.join(OUT_DIR, 'archive-all-idle.png'), clip });

  await btn.click();
  // Wait until the loading class is applied so the screenshot is deterministic.
  await page.waitForFunction(() => {
    const b = document.querySelector('[data-testid="archive-all-btn"]') as HTMLElement | null;
    return !!b && b.classList.contains('column__archive-all--loading');
  }, undefined, { timeout: 2_000 });
  await page.screenshot({ path: path.join(OUT_DIR, 'archive-all-loading.png'), clip });

  // Full-board context shot too, easier to read in a PR review.
  await page.screenshot({ path: path.join(OUT_DIR, 'archive-all-loading-board.png'), fullPage: false });
});
