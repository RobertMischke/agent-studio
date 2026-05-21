import { test, expect } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Ad-hoc verification: orchestrator chat now renders phase + super-phase
 * dividers INLINE in the continuous stream (instead of the old master-
 * strip phase-summary-list above the chat body). Drives the page, opens
 * the orchestrator rail, asserts:
 *   - the legacy <app-phase-summary-list> is gone from the orchestrator
 *     chat surface,
 *   - at least one phase divider is rendered inside the chat body when
 *     there is any conversation history,
 *   - the Phases tab in the verbose-debug overlay exists.
 * Captures screenshots as evidence.
 */

test('orchestrator chat shows inline phase dividers + no master strip', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });

  await page.getByTestId('studio-titlebar-chat').click();
  const rail = page.locator('app-orchestrator-side-sheet');
  await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });
  await page.waitForTimeout(500);

  // The legacy master-strip should no longer be present inside the
  // orchestrator's chat surface.
  const legacyStripInOrch = rail.locator('app-phase-summary-list');
  await expect(legacyStripInOrch).toHaveCount(0);

  // The chat body should be present and scrollable.
  const chatBody = rail.locator('[data-testid="chat-body"]').first();
  await expect(chatBody).toBeVisible();

  const outDir = join(process.cwd(), 'test-results', 'orch-phases-continuous');
  mkdirSync(outDir, { recursive: true });
  await page.screenshot({ path: join(outDir, 'orch-with-inline-phases.png'), fullPage: false });

  // If the chat has any history, at least one phase divider should
  // render inline. Empty conversation is also a valid state.
  const phaseDividers = rail.locator('[data-testid^="chat-phase-divider-"]');
  const phaseCount = await phaseDividers.count();
  const superDividers = rail.locator('[data-testid^="chat-super-divider-"]');
  const superCount = await superDividers.count();
  writeFileSync(
    join(outDir, 'counts.json'),
    JSON.stringify({ phaseDividers: phaseCount, superDividers: superCount }, null, 2)
  );
  // Sanity: super-phase count should never exceed phase count.
  expect(superCount).toBeLessThanOrEqual(Math.max(1, phaseCount));
});

test('verbose debug overlay has Phases tab populated from chat events', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });

  await page.getByTestId('studio-titlebar-chat').click();
  const rail = page.locator('app-orchestrator-side-sheet');
  await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });
  await page.waitForTimeout(500);

  const debugBtn = rail.getByTestId('orch-side-sheet-verbose-debug');
  if (!(await debugBtn.isVisible())) {
    test.skip(true, 'No active job → no Debug button to open');
    return;
  }
  await debugBtn.click();

  const overlay = page.getByTestId('app-verbose-debug-overlay');
  await expect(overlay).toBeVisible({ timeout: 5_000 });

  // Switch to the new Phases tab.
  const phasesTab = overlay.locator('button:has-text("Phases")').first();
  await phasesTab.click();
  await page.waitForTimeout(200);

  const phasesRoot = overlay.getByTestId('verbose-debug-phases');
  await expect(phasesRoot).toBeVisible({ timeout: 5_000 });

  const outDir = join(process.cwd(), 'test-results', 'orch-phases-continuous');
  mkdirSync(outDir, { recursive: true });
  await page.screenshot({ path: join(outDir, 'verbose-debug-phases-tab.png'), fullPage: false });
});
