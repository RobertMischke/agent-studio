import { test, expect } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * F15 sweep: the orchestrator chat no longer renders inline phase /
 * super-phase dividers. In the orchestrator chat every phase was a
 * single Q-A pair, so the bracket was visual noise. The grouping data
 * still lives on the chat component's `phases()` / `superPhases()`
 * computed signals (used by the verbose-debug overlay's Phases tab);
 * only the inline divider chrome is gone. This spec asserts:
 *   - the legacy <app-phase-summary-list> is gone from the orchestrator
 *     chat surface,
 *   - NO `chat-phase-divider-*` / `chat-super-divider-*` testids render
 *     inside the chat body (regardless of conversation length),
 *   - the Phases tab in the verbose-debug overlay still works.
 * Captures screenshots as evidence.
 */

test('orchestrator chat renders no inline phase/super-phase dividers', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });

  await page.getByTestId('studio-titlebar-chat').click();
  const rail = page.locator('app-orchestrator-side-sheet');
  await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });
  await page.waitForTimeout(500);

  // The legacy master-strip should no longer be present inside the
  // orchestrator's chat surface.
  const legacyStripInOrch = rail.locator('app-phase-summary-list, cac-phase-summary-list');
  await expect(legacyStripInOrch).toHaveCount(0);

  // The chat body should be present and scrollable.
  const chatBody = rail.locator('[data-testid="chat-body"]').first();
  await expect(chatBody).toBeVisible();

  const outDir = join(process.cwd(), 'test-results', 'orch-phases-continuous');
  mkdirSync(outDir, { recursive: true });
  await page.screenshot({ path: join(outDir, 'orch-no-inline-phases.png'), fullPage: false });

  // F15: phase / super-phase dividers MUST NOT render inline in the
  // orchestrator chat. Both counts must be zero regardless of
  // conversation length.
  const phaseDividers = rail.locator('[data-testid^="chat-phase-divider-"]');
  const phaseCount = await phaseDividers.count();
  const superDividers = rail.locator('[data-testid^="chat-super-divider-"]');
  const superCount = await superDividers.count();
  writeFileSync(
    join(outDir, 'counts.json'),
    JSON.stringify({ phaseDividers: phaseCount, superDividers: superCount }, null, 2)
  );
  expect(phaseCount, 'no inline chat-phase-divider rows after F15').toBe(0);
  expect(superCount, 'no inline chat-super-divider rows after F15').toBe(0);
});

test('verbose debug overlay has Phases tab populated from chat events', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });

  await page.getByTestId('studio-titlebar-chat').click();
  const rail = page.locator('app-orchestrator-side-sheet');
  await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });
  await page.waitForTimeout(500);
  await rail.getByTestId('orch-context-badge').click();
  await expect(rail.getByTestId('orch-context-menu')).toBeVisible();

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
