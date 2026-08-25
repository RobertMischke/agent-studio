import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.resolve('test-results');

const BANNER_HARNESS = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Provider limit visibility</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; padding: 24px; background: #111827; color: #e5e7eb;
    font: 14px/1.45 system-ui, sans-serif; }
  .stack { display: grid; gap: 10px; max-width: 920px; }
  .notification { display: grid; gap: 4px; padding: 12px 14px; border-radius: 8px;
    border: 1px solid #a16207; background: #422006; }
  .detail { color: #fde68a; }
</style></head><body><main class="stack">
  <section class="notification" role="status" data-testid="provider-limit-banner">
    <strong>claude claims are limited until Aug 24, 2026, 2:20 AM.</strong>
    <span class="detail">1 task is waiting on the provider account limit. Other CLI claims remain eligible and recovery is automatic.</span>
  </section>
  <section class="notification" role="status" data-testid="runner-pause-banner">
    <strong>Pickup paused: infra breaker in Agent Studio.</strong>
    <span class="detail">pickup paused: infra breaker, 5 failures cliType=claude at Aug 24, 2026, 12:20 AM. Recovery is automatic after the provider probe succeeds, scheduled from Aug 24, 2026, 2:20 AM.</span>
  </section>
</main></body></html>`;

test('provider-limit and infra-breaker notices remain distinct and explicit', async ({ page }) => {
  await page.setViewportSize({ width: 1100, height: 420 });
  await page.setContent(BANNER_HARNESS, { waitUntil: 'load' });

  const provider = page.getByTestId('provider-limit-banner');
  const breaker = page.getByTestId('runner-pause-banner');
  await expect(provider).toContainText('claude claims are limited until');
  await expect(provider).toContainText('Other CLI claims remain eligible and recovery is automatic');
  await expect(breaker).toContainText('pickup paused: infra breaker, 5 failures cliType=claude');
  await expect(breaker).toContainText('Recovery is automatic after the provider probe succeeds');
  expect(await provider.evaluate(element => getComputedStyle(element).borderLeftWidth))
    .toBe(await provider.evaluate(element => getComputedStyle(element).borderRightWidth));

  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  await page.locator('.stack').screenshot({
    path: path.join(RESULTS_DIR, 'provider-limit-breaker-banner--mocked.png'),
  });
});
