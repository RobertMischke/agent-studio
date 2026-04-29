import { test, expect } from '@playwright/test';
import { api } from './helpers/api';

// On-demand README screenshot generator. Skips itself unless the backend is
// pointed at the temporary "Sample Shop" demo workspace, so it stays a no-op
// during normal test runs. To regenerate screenshots, swap WatchPaths in
// backend/appsettings.Development.json to a Sample Shop demo and run only
// this spec. Output paths are relative to the frontend/ working dir.

interface WatchPath { name?: string }

const OUT = '../docs/images/';

test.describe.configure({ mode: 'serial' });

test.use({ viewport: { width: 1440, height: 900 } });

test('readme screenshots — board, detail, protocol pane', async ({ page }) => {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  test.skip(!paths.some(p => p.name === 'Sample Shop'),
    'Sample Shop demo workspace not configured — skipping README screenshot regeneration');

  await page.goto('/');
  await expect(page.getByTestId('job-card').first()).toBeVisible({ timeout: 15_000 });
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}board-overview.png`, fullPage: false });

  await page.locator('[data-testid="job-card"]').filter({ hasText: 'coffee' }).first().click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${OUT}detail-protocol.png`, fullPage: false });

  await page.getByTestId('pane-toggle-git').click();
  await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 5_000 });
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}detail-three-panes.png`, fullPage: false });

  await page.getByTestId('back-to-board').click();
  await expect(page.getByTestId('job-card').first()).toBeVisible({ timeout: 10_000 });

  await page.locator('[data-testid="job-card"]').filter({ hasText: 'wishlist' }).first().click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${OUT}detail-quality-gate.png`, fullPage: false });
});
