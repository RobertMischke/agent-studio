import { test, expect } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Verification probe: orchestrator chat now sits as a column INSIDE the
 * studio-shell body grid (between titlebar and statusbar). It should no
 * longer push the statusbar / activity bar leftward.
 *
 * Captures: full-viewport screenshot with the orchestrator open + bbox
 * geometry of titlebar, statusbar, activity bar, and orchestrator side
 * sheet so we can assert the same outer container hosts all four.
 */
test('orchestrator side sheet renders inside studio-shell body grid', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');

  const studioRoot = page.locator('[data-studio="root"]');
  await expect(studioRoot).toBeVisible({ timeout: 15_000 });

  await page.getByTestId('studio-titlebar-chat').click();
  const rail = page.locator('app-orchestrator-side-sheet');
  await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });
  await page.waitForTimeout(400);

  const outDir = join(process.cwd(), 'test-results', 'verify-orchestrator-inside-shell');
  mkdirSync(outDir, { recursive: true });
  await page.screenshot({ path: join(outDir, 'orchestrator-open.png'), fullPage: false });

  // Capture bounding boxes for chrome elements.
  const titlebar = studioRoot.locator('.studio-titlebar').first();
  const statusbar = page.locator('[data-testid="studio-statusbar"]').first();
  const activityBar = page.locator('[data-testid="studio-activity-bar"]').first();
  const studioBody = studioRoot.locator('.studio-body').first();

  const studioBox = await studioRoot.boundingBox();
  const titlebarBox = await titlebar.boundingBox().catch(() => null);
  const statusbarBox = await statusbar.boundingBox().catch(() => null);
  const activityBox = await activityBar.boundingBox().catch(() => null);
  const studioBodyBox = await studioBody.boundingBox().catch(() => null);
  const railBox = await rail.boundingBox();

  const report = {
    viewport: { width: 1600, height: 1000 },
    studioRoot: studioBox,
    titlebar: titlebarBox,
    statusbar: statusbarBox,
    activityBar: activityBox,
    studioBody: studioBodyBox,
    orchestrator: railBox,
  };
  writeFileSync(join(outDir, 'geometry.json'), JSON.stringify(report, null, 2));

  // Sanity: all four chrome elements exist.
  expect(titlebarBox, 'titlebar present').not.toBeNull();
  expect(statusbarBox, 'statusbar present').not.toBeNull();
  expect(activityBox, 'activity bar present').not.toBeNull();
  expect(railBox, 'orchestrator side sheet present').not.toBeNull();

  // Titlebar should span the full studio-root width (within 4px tolerance).
  expect(Math.abs((titlebarBox!.width) - (studioBox!.width))).toBeLessThanOrEqual(4);
  // Statusbar should also span the full studio-root width.
  expect(Math.abs((statusbarBox!.width) - (studioBox!.width))).toBeLessThanOrEqual(4);
  // Activity bar should sit at the left edge of studio-root.
  expect(Math.abs((activityBox!.x) - (studioBox!.x))).toBeLessThanOrEqual(4);
  // Orchestrator should sit BELOW the titlebar and ABOVE the statusbar.
  expect(railBox!.y).toBeGreaterThanOrEqual(titlebarBox!.y + titlebarBox!.height - 1);
  expect(railBox!.y + railBox!.height).toBeLessThanOrEqual(statusbarBox!.y + 1);
});
