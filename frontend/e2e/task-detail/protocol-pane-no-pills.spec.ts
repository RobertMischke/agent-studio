import { test, expect } from '@playwright/test';
import { listJobs } from '../helpers/jobs';
import { api } from '../helpers/api';

interface JobDetail {
  info: { id: string; watchPath: string };
  statusMarkdown: string | null;
}

async function pickJobWithProtocol(): Promise<JobDetail | null> {
  for (const j of await listJobs()) {
    try {
      const detail = await api<JobDetail>(
        `/api/jobs/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (detail.statusMarkdown && detail.statusMarkdown.length > 10) return detail;
    } catch { /* skip */ }
  }
  return null;
}

/**
 * F63 — Rendered/Raw pill buttons removed from protocol pane.
 *
 * F54 moved the toggle into the context menu. F63 removes the leftover
 * pill buttons that were still visible below the verdict banner.
 */
test.describe('Protocol pane — no Rendered/Raw pills (F63)', () => {
  test('no render-mode pill buttons in protocol tab; context menu toggle still works', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });
    const detail = await pickJobWithProtocol();
    if (!detail) {
      test.skip(true, 'No job with status.md found');
      return;
    }

    await page.goto(
      `/?job=${encodeURIComponent(detail.info.id)}&watchPath=${encodeURIComponent(detail.info.watchPath)}`
    );

    const protocolTab = page.getByTestId('inspector-tab-protocol');
    await expect(protocolTab).toBeVisible({ timeout: 10_000 });
    await protocolTab.click();

    // 1. No Rendered/Raw pill buttons anywhere in the DOM
    await expect(page.locator('[data-testid="results-view-mode-rendered"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="results-view-mode-raw"]')).toHaveCount(0);
    await expect(page.locator('.results-view__mode')).toHaveCount(0);
    await expect(page.locator('.results-view__modes')).toHaveCount(0);

    // 2. The toolbar still has copy + more-actions buttons
    await expect(page.getByTestId('protocol-copy-markdown')).toBeVisible();
    const moreBtn = page.getByTestId('protocol-more-actions');
    await expect(moreBtn).toBeVisible();

    // 3. Context menu toggle still works (F54 contract)
    await moreBtn.click();
    const menuPanel = page.getByTestId('protocol-context-menu-panel');
    await expect(menuPanel).toBeVisible({ timeout: 3_000 });
    await expect(page.getByTestId('protocol-context-menu-item-view-rendered')).toBeVisible();
    await expect(page.getByTestId('protocol-context-menu-item-view-raw')).toBeVisible();

    // 4. Switch to raw via context menu — no pill appears
    await page.getByTestId('protocol-context-menu-item-view-raw').click();
    await expect(page.getByTestId('protocol-raw-markdown')).toBeVisible({ timeout: 3_000 });
    await expect(page.locator('[data-testid="results-view-mode-rendered"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="results-view-mode-raw"]')).toHaveCount(0);

    await page.screenshot({
      path: 'test-results/f63-protocol-pane-no-pills.png',
      fullPage: false,
    });
  });
});
