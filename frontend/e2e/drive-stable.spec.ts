import { expect, test } from '@playwright/test';
import * as path from 'path';

/**
 * Drive specs for the agent-as-User loop. NOT intended for CI - these are
 * orchestration helpers the agent invokes via PW_TARGET=stable to capture
 * what the user would see while monitoring an in-flight job.
 */

test('snapshot: kanban with running job', async ({ page, baseURL }) => {
  console.log(`[drive] kanban snapshot @ ${baseURL}`);
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2000);
  const out = path.join('test-results', 'drive-kanban.png');
  await page.screenshot({ path: out, fullPage: true });
});

test('snapshot: open running job protocol view', async ({ page, baseURL }) => {
  console.log(`[drive] running-job protocol view @ ${baseURL}`);
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2000);

  // Click the In Progress card if any exists
  const card = page.locator('[data-testid^="job-card-"]').filter({ hasText: /Images and protocol|images-and-protocol/i }).first();
  await card.click({ timeout: 5000 }).catch(() => { /* card might not be clickable; ok */ });
  await page.waitForTimeout(1500);

  const out = path.join('test-results', 'drive-job-protocol.png');
  await page.screenshot({ path: out, fullPage: true });
});
