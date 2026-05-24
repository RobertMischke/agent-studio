import { test, expect } from '@playwright/test';

/**
 * F50: Each CLI quota card shows both 5H and WK window cells when the
 * backend provides both windows. CLIs that only have one window type
 * show only that cell (no empty placeholder).
 */

function mockQuotaReport(opts?: { copilotWeeklyOnly?: boolean }) {
  return {
    at: new Date().toISOString(),
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'claude',
        fetchedAt: new Date().toISOString(),
        plan: 'max_5',
        windows: [
          { label: '5-hour rolling', usedPct: 31, used: 12300, limit: 40000, unit: 'requests', resetAt: null, resetLabel: 'in 1h 47m' },
          { label: 'Weekly', usedPct: 55, used: 220000, limit: 400000, unit: 'requests', resetAt: null, resetLabel: 'in 4d 3h' },
        ],
        source: 'mock',
        rawSample: null,
        error: null,
      },
      {
        cliType: 'codex',
        fetchedAt: new Date().toISOString(),
        plan: 'pro',
        windows: [
          { label: '5h session', usedPct: 72, used: 2880, limit: 4000, unit: 'requests', resetAt: null, resetLabel: 'in 2h 10m' },
          { label: 'weekly', usedPct: 40, used: 40000, limit: 100000, unit: 'requests', resetAt: null, resetLabel: 'in 5d' },
        ],
        source: 'mock',
        rawSample: null,
        error: null,
      },
      {
        cliType: 'copilot',
        fetchedAt: new Date().toISOString(),
        plan: 'business',
        windows: opts?.copilotWeeklyOnly !== false
          ? [{ label: 'Weekly premium', usedPct: 18, used: 90, limit: 500, unit: 'requests', resetAt: null, resetLabel: 'in 6d' }]
          : [
              { label: '5-hour rolling', usedPct: 10, used: 50, limit: 500, unit: 'requests', resetAt: null, resetLabel: 'in 3h' },
              { label: 'Weekly premium', usedPct: 18, used: 90, limit: 500, unit: 'requests', resetAt: null, resetLabel: 'in 6d' },
            ],
        source: 'mock',
        rawSample: null,
        error: null,
      },
    ],
  };
}

test.describe('Status bar quota: dual window cells (5H + WK)', () => {
  test('Claude card shows both 5H and WK cells with values and bars', async ({ page }) => {
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(mockQuotaReport()),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);

    const claude5h = page.getByTestId('hquota-claude-5h');
    const claudeWk = page.getByTestId('hquota-claude-wk');

    await expect(claude5h).toBeVisible();
    await expect(claudeWk).toBeVisible();

    // Value text is present.
    await expect(claude5h.locator('.hquota__window-value')).toContainText('31%');
    await expect(claudeWk.locator('.hquota__window-value')).toContainText('55%');

    // Label text.
    await expect(claude5h.locator('.hquota__window-label')).toContainText('5H');
    await expect(claudeWk.locator('.hquota__window-label')).toContainText('WK');

    // Both have a bar.
    await expect(claude5h.locator('.hquota__window-bar-fill')).toBeVisible();
    await expect(claudeWk.locator('.hquota__window-bar-fill')).toBeVisible();

    // Screenshot of the Claude card detail.
    const claudeCard = page.getByTestId('hquota-card-claude');
    await claudeCard.screenshot({ path: 'test-results/f50-status-bar-quota-detail-claude.png' });
  });

  test('Codex card shows both 5H and WK cells', async ({ page }) => {
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(mockQuotaReport()),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);

    await expect(page.getByTestId('hquota-codex-5h')).toBeVisible();
    await expect(page.getByTestId('hquota-codex-wk')).toBeVisible();

    await expect(page.getByTestId('hquota-codex-5h').locator('.hquota__window-value')).toContainText('72%');
    await expect(page.getByTestId('hquota-codex-wk').locator('.hquota__window-value')).toContainText('40%');
  });

  test('Copilot card shows only WK cell when backend provides only Weekly', async ({ page }) => {
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(mockQuotaReport({ copilotWeeklyOnly: true })),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);

    // WK cell visible.
    await expect(page.getByTestId('hquota-copilot-wk')).toBeVisible();

    // 5H cell does NOT exist.
    await expect(page.getByTestId('hquota-copilot-5h')).toHaveCount(0);
  });

  test('tone coloring applies per window cell independently', async ({ page }) => {
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(mockQuotaReport()),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);

    // Codex 5h is 72% -> warn tone.
    await expect(page.getByTestId('hquota-codex-5h')).toHaveAttribute('data-tone', 'warn');
    // Codex WK is 40% -> ok tone.
    await expect(page.getByTestId('hquota-codex-wk')).toHaveAttribute('data-tone', 'ok');
  });

  test('full status bar screenshot in both themes', async ({ page }) => {
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(mockQuotaReport()),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);

    // Dark theme screenshot (default).
    const statusBar = page.getByTestId('status-bar');
    await statusBar.screenshot({ path: 'test-results/f50-status-bar-full-dark.png' });

    // Toggle to light theme if a toggle exists.
    const themeToggle = page.getByTestId('theme-toggle');
    if (await themeToggle.count() > 0) {
      await themeToggle.click();
      await page.waitForTimeout(300);
      await statusBar.screenshot({ path: 'test-results/f50-status-bar-full-light.png' });
    }

    // Narrow viewport.
    await page.setViewportSize({ width: 1000, height: 900 });
    await page.waitForTimeout(500);
    await statusBar.screenshot({ path: 'test-results/f50-status-bar-narrow-viewport.png' });
  });
});
