import { expect, test } from '@playwright/test';
import * as path from 'path';

/**
 * Smoke proof for the Playwright-as-User cycle.
 *
 * Drives whichever stack the playwright.config.ts target resolves to (stable
 * in the agent-driven loop), waits for the kanban to render, and dumps a
 * full-page PNG into test-results/ so the agent can paste it back into chat.
 *
 * No state mutation: this only loads the page and captures it.
 */
test('smoke: stable kanban renders and is captured', async ({ page, baseURL }) => {
  console.log(`[smoke] baseURL = ${baseURL}`);
  const response = await page.goto('/', { waitUntil: 'domcontentloaded' });
  expect(response?.ok(), `expected 2xx from ${baseURL}/, got ${response?.status()}`).toBeTruthy();

  await page.waitForLoadState('networkidle').catch(() => { /* polling app: networkidle may never fire */ });
  await page.waitForTimeout(1500);

  const out = path.join('test-results', 'smoke-stable.png');
  await page.screenshot({ path: out, fullPage: true });
  console.log(`[smoke] screenshot -> ${out}`);
});
