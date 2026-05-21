import { test, expect } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Ad-hoc verification: orchestrator side-sheet is now hosted inside
 * studio-shell (right-edge grid column of studio-body) instead of
 * being a sibling of studio-shell in .app-shell. Drives the page,
 * opens the rail via the titlebar "Project Chat" button, then asserts
 * that the rail's bounding box is contained within studio-shell's box
 * (specifically: rail.right <= shell.right + tolerance, rail.top >=
 * titlebar.bottom, rail.bottom <= statusbar.top). Captures a
 * full-page screenshot as evidence.
 */

test('orchestrator rail sits inside studio-shell body', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');
  // Wait for studio-shell to mount.
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });

  const titlebarChat = page.getByTestId('studio-titlebar-chat');
  await expect(titlebarChat).toBeVisible();
  await titlebarChat.click();

  // The orchestrator host gets .is-open when toggled open. Use the
  // element selector since the host has no data-testid; the host's
  // animated width grows from 0 → ~640.
  const rail = page.locator('app-orchestrator-side-sheet');
  await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });
  // Give the width transition a moment to settle.
  await page.waitForTimeout(400);

  const studio = page.locator('[data-studio="root"]');
  const titlebar = page.locator('.studio-titlebar');
  const statusbar = page.getByTestId('studio-statusbar');

  const [studioBox, titleBox, statusBox, railBox] = await Promise.all([
    studio.boundingBox(),
    titlebar.boundingBox(),
    statusbar.boundingBox(),
    rail.boundingBox(),
  ]);

  if (!studioBox || !titleBox || !statusBox || !railBox) {
    throw new Error(
      `Missing bounding boxes: studio=${!!studioBox} title=${!!titleBox} status=${!!statusBox} rail=${!!railBox}`
    );
  }

  // Evidence dump.
  const outDir = join(process.cwd(), 'test-results', 'orch-inside-shell');
  mkdirSync(outDir, { recursive: true });
  writeFileSync(
    join(outDir, 'boxes.json'),
    JSON.stringify({ studio: studioBox, titlebar: titleBox, statusbar: statusBox, rail: railBox }, null, 2)
  );
  await page.screenshot({ path: join(outDir, 'shell-with-orchestrator.png'), fullPage: false });

  // Containment: rail must be inside studio-shell box, and inside
  // the body (between titlebar bottom and statusbar top).
  const tol = 2; // sub-pixel rounding tolerance
  expect(railBox.x, 'rail left should be within studio-shell').toBeGreaterThanOrEqual(studioBox.x - tol);
  expect(railBox.x + railBox.width, 'rail right should be within studio-shell').toBeLessThanOrEqual(
    studioBox.x + studioBox.width + tol
  );
  expect(railBox.y, 'rail top should be below titlebar').toBeGreaterThanOrEqual(titleBox.y + titleBox.height - tol);
  expect(railBox.y + railBox.height, 'rail bottom should be above statusbar').toBeLessThanOrEqual(
    statusBox.y + tol
  );

  // Titlebar and statusbar should span the full studio-shell width.
  expect(titleBox.width, 'titlebar should span studio-shell width').toBeGreaterThanOrEqual(studioBox.width - tol);
  expect(statusBox.width, 'statusbar should span studio-shell width').toBeGreaterThanOrEqual(studioBox.width - tol);

  // Probe: close the rail again via the same toggle. The host should
  // animate back to width 0 so the editor reclaims the space; the
  // trailing `auto` grid track must not retain phantom width.
  await titlebarChat.click();
  await expect(rail).not.toHaveClass(/is-open/, { timeout: 5_000 });
  await page.waitForTimeout(400);
  const closedRailBox = await rail.boundingBox();
  if (!closedRailBox) throw new Error('rail bounding box missing after close');
  await page.screenshot({ path: join(outDir, 'shell-orchestrator-closed.png'), fullPage: false });
  expect(closedRailBox.width, 'rail should collapse to ~0 when closed').toBeLessThanOrEqual(2);
});
