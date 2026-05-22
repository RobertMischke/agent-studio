import { test } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const ARTIFACT_DIR = String.raw`c:\Projects\agent-taskboard-devspace\artifacts\test-runs\20260521-0923-todo-app`;

test('capture final state and render of the produced todo app', async ({ page }) => {
  mkdirSync(ARTIFACT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1000 });

  // 1. Final taskboard state.
  await page.goto('/');
  await page.locator('[data-studio="root"]').waitFor({ state: 'visible', timeout: 15_000 });
  await page.getByTestId('studio-project-picker-trigger').click();
  await page.getByTestId('studio-project-picker-item-Runbook').click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: join(ARTIFACT_DIR, 'final-board.png'), fullPage: false });

  // 2. Open our task in the detail view to inspect verdicts.
  const ours = page.locator('[data-testid^="job-card-"]').filter({ hasText: 'Playwright probe' }).first();
  if (await ours.isVisible()) {
    await ours.click();
    await page.waitForTimeout(1500);
    await page.screenshot({ path: join(ARTIFACT_DIR, 'final-detail.png'), fullPage: false });
  } else {
    writeFileSync(join(ARTIFACT_DIR, 'final-detail-missing.txt'), 'Could not find Playwright probe card in the visible board.');
  }

  // 3. Render the produced index.html to confirm the agent's deliverable.
  await page.goto('file:///C:/Projects/Runbook/App/scratch/playwright-probe-todo/index.html');
  await page.waitForTimeout(400);
  await page.locator('#todo-input').fill('First task');
  await page.locator('#add-btn').click();
  await page.locator('#todo-input').fill('Second task');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(200);
  await page.screenshot({ path: join(ARTIFACT_DIR, 'final-todo-app.png'), fullPage: false });
});
