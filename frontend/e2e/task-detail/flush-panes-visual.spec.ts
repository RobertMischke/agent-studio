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
        `/api/tasks/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (detail.statusMarkdown && detail.statusMarkdown.length > 10) return detail;
    } catch { /* skip */ }
  }
  return null;
}

/**
 * Flush panes — visual verification.
 *
 * Confirms that the prompt and protocol panes sit directly on the
 * detail-view surface without card chrome (no own border, border-radius,
 * or box-shadow).
 */
test.describe('Flush panes — no card chrome', () => {
  test('panes have no border, border-radius, or box-shadow', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });
    const detail = await pickJobWithProtocol();
    if (!detail) {
      test.skip(true, 'No job with status.md found');
      return;
    }

    await page.goto(
      `/?job=${encodeURIComponent(detail.info.id)}&watchPath=${encodeURIComponent(detail.info.watchPath)}`
    );

    const promptPane = page.getByTestId('pane-prompt');
    const protocolPane = page.getByTestId('pane-protocol');
    await expect(promptPane).toBeVisible({ timeout: 10_000 });
    await expect(protocolPane).toBeVisible();

    // Verify no card chrome on prompt pane
    const promptStyles = await promptPane.evaluate(el => {
      const cs = getComputedStyle(el);
      return {
        border: cs.border,
        borderRadius: cs.borderRadius,
        boxShadow: cs.boxShadow,
        background: cs.background,
      };
    });
    expect(promptStyles.borderRadius).toBe('0px');
    expect(promptStyles.boxShadow).toBe('none');
    // Border should be "none" or "0px none ..."
    expect(promptStyles.border).toMatch(/\b(none|0px)\b/);

    // Verify no card chrome on protocol pane
    const protocolStyles = await protocolPane.evaluate(el => {
      const cs = getComputedStyle(el);
      return {
        border: cs.border,
        borderRadius: cs.borderRadius,
        boxShadow: cs.boxShadow,
        background: cs.background,
      };
    });
    expect(protocolStyles.borderRadius).toBe('0px');
    expect(protocolStyles.boxShadow).toBe('none');
    expect(protocolStyles.border).toMatch(/\b(none|0px)\b/);

    // Verify the splitter between panes is thin (1px, not 10px)
    const splitter = page.locator('.pane__splitter').first();
    if (await splitter.count() > 0) {
      const splitterWidth = await splitter.evaluate(el => {
        return parseFloat(getComputedStyle(el).flexBasis);
      });
      expect(splitterWidth).toBeLessThanOrEqual(3);
    }

    // Verify pane headers have transparent background
    const promptHeader = page.getByTestId('pane-prompt-header');
    if (await promptHeader.count() > 0) {
      const headerBg = await promptHeader.evaluate(el => {
        return getComputedStyle(el).backgroundColor;
      });
      // transparent = rgba(0, 0, 0, 0)
      expect(headerBg).toBe('rgba(0, 0, 0, 0)');
    }

    // Screenshot — light theme
    await page.screenshot({
      path: 'test-results/flush-panes-light.png',
      fullPage: false,
    });

    // Dismiss any notification toasts that might block clicks
    for (const dismiss of await page.locator('[data-testid*="notification"] button:has-text("×"), [data-testid*="notification"] button:has-text("Dismiss")').all()) {
      await dismiss.click({ force: true }).catch(() => {});
    }
    await page.waitForTimeout(200);

    // Switch theme for the second screenshot
    const themeToggle = page.locator('button[title*="Switch to"]');
    if (await themeToggle.count() > 0) {
      await themeToggle.first().click({ force: true });
      await page.waitForTimeout(500);
      await page.screenshot({
        path: 'test-results/flush-panes-alt-theme.png',
        fullPage: false,
      });
      // Switch back
      await themeToggle.first().click({ force: true });
    }
  });
});
