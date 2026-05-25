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
 * F54 — Protocol-pane chrome cleanup.
 *
 * Verifies that the hygiene strip, top-level Regenerate button, and
 * Rendered/Raw toggle are removed from the protocol pane body, and that
 * the context menu surfaces Regenerate + View raw options.
 */
test.describe('Protocol pane chrome cleanup (F54)', () => {
  test('hygiene strip removed, no top-level regen/raw toggle, context menu available', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });
    const detail = await pickJobWithProtocol();
    if (!detail) {
      test.skip(true, 'No job with status.md found — cannot test protocol chrome');
      return;
    }

    await page.goto(
      `/?job=${encodeURIComponent(detail.info.id)}&watchPath=${encodeURIComponent(detail.info.watchPath)}`
    );

    // Switch to Protocol tab
    const protocolTab = page.getByTestId('inspector-tab-protocol');
    await expect(protocolTab).toBeVisible({ timeout: 10_000 });
    await protocolTab.click();

    // 1. Hygiene strip must NOT be in the DOM
    await expect(page.locator('app-hygiene-strip')).toHaveCount(0);
    await expect(page.getByTestId('hygiene-strip')).toHaveCount(0);

    // 2. No visible Rendered/Raw toggle pill group (data-testid pattern)
    await expect(page.locator('[data-testid*="raw-toggle"]')).toHaveCount(0);

    // 3. No top-level btn-regen outside the summary-error section
    //    The only btn-regen remaining is the "Try again" inside the error banner
    const topLevelRegen = page.locator('.notes-panel__toolbar .btn-regen');
    await expect(topLevelRegen).toHaveCount(0);

    // 4. The toolbar has the "..." more-actions button
    const moreBtn = page.getByTestId('protocol-more-actions');
    await expect(moreBtn).toBeVisible();

    // 5. Open context menu
    await moreBtn.click();
    const menuPanel = page.getByTestId('protocol-context-menu-panel');
    await expect(menuPanel).toBeVisible({ timeout: 3_000 });

    // 6. Menu items: Regenerate + View rendered + View raw markdown
    await expect(page.getByTestId('protocol-context-menu-item-regenerate')).toBeVisible();
    await expect(page.getByTestId('protocol-context-menu-item-view-rendered')).toBeVisible();
    await expect(page.getByTestId('protocol-context-menu-item-view-raw')).toBeVisible();

    await page.screenshot({
      path: 'test-results/f54-protocol-pane-context-menu-open.png',
      fullPage: false,
    });

    // 7. Click "View raw markdown" → raw pre block appears
    await page.getByTestId('protocol-context-menu-item-view-raw').click();
    const rawPre = page.getByTestId('protocol-raw-markdown');
    await expect(rawPre).toBeVisible({ timeout: 3_000 });
    // Beautiful-results should be gone
    await expect(page.getByTestId('protocol-beautiful-results')).toHaveCount(0);

    await page.screenshot({
      path: 'test-results/f54-protocol-pane-raw-mode.png',
      fullPage: false,
    });

    // 8. Switch back via context menu → rendered
    await moreBtn.click();
    await expect(menuPanel).toBeVisible({ timeout: 3_000 });
    await page.getByTestId('protocol-context-menu-item-view-rendered').click();
    await expect(page.getByTestId('protocol-beautiful-results')).toBeVisible({ timeout: 3_000 });
    await expect(rawPre).toHaveCount(0);

    await page.screenshot({
      path: 'test-results/f54-protocol-pane-after-light.png',
      fullPage: false,
    });
  });
});
