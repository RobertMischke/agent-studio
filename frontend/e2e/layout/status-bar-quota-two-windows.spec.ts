import { test, expect } from '@playwright/test';

/**
 * F50 follow-up: each CLI quota card renders one uniform primary pill -
 * the most-constraining (highest used%) window across all windows the
 * backend reports - with an identical icon + name + value + tag + bar
 * shape. The full per-window breakdown lives in the tooltip / detail
 * modal, so the strip itself stays on a single clean, readable line.
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
          ? [{ label: 'Monthly premium', usedPct: 18, used: 90, limit: 500, unit: 'requests', resetAt: null, resetLabel: 'in 6d' }]
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

test.describe('Status bar quota: uniform primary pill', () => {
  test('Claude pill shows the most-constraining window (Weekly 55%)', async ({ page }) => {
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

    const claude = page.getByTestId('hquota-claude-primary');
    await expect(claude).toBeVisible();

    // Highest used% across Claude's windows is the Weekly window (55%).
    await expect(claude.locator('.hquota__value')).toContainText('55%');
    await expect(claude.locator('.hquota__tag')).toContainText('WK');
    await expect(claude.locator('.hquota__bar-fill')).toBeVisible();

    const claudeCard = page.getByTestId('hquota-card-claude');
    await claudeCard.screenshot({ path: 'test-results/f50-status-bar-quota-detail-claude.png' });
  });

  test('Codex pill shows its 5H window (72%) as the primary', async ({ page }) => {
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

    const codex = page.getByTestId('hquota-codex-primary');
    await expect(codex).toBeVisible();
    await expect(codex.locator('.hquota__value')).toContainText('72%');
    await expect(codex.locator('.hquota__tag')).toContainText('5H');
  });

  // Regression for the "Copilot quota windows empty / error pill" bug: the
  // backend probe now reports the home-screen-footer "Remaining reqs." figure
  // as a single monthly premium-requests window. This pins that the pill shows
  // a real value (not blank / not the red error state) for that exact shape.
  test('Copilot pill is non-empty for the footer-derived monthly window', async ({ page }) => {
    const report = {
      at: new Date().toISOString(),
      ttlSeconds: 600,
      snapshots: [
        {
          cliType: 'copilot',
          fetchedAt: new Date().toISOString(),
          plan: 'Pro',
          // Exact shape CopilotQuotaProbe.ParseSnapshot emits for "Remaining reqs.: 71.1%".
          windows: [
            { label: 'Premium requests (monthly)', usedPct: 28.9, used: 87, limit: 300, unit: 'requests', resetAt: '2026-07-01T00:00:00Z', resetLabel: 'Jul 1' },
          ],
          source: 'home-screen footer',
          rawSample: 'Remaining reqs.: 71.1%',
          error: null,
        },
      ],
    };

    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(report) });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);

    const card = page.getByTestId('hquota-card-copilot');
    const copilot = page.getByTestId('hquota-copilot-primary');
    await expect(copilot).toBeVisible();

    // Non-empty: a concrete "%" value, not the muted "—" placeholder.
    const value = copilot.locator('.hquota__value');
    await expect(value).toContainText('29%');
    await expect(value).not.toHaveText('—');
    await expect(copilot.locator('.hquota__tag')).toContainText('MO');

    // Not the error state (clean data, no red pill).
    await expect(card).toHaveAttribute('data-state', /^(idle|stale|warn|hot)$/);

    const outDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
    await card.screenshot({ path: `${outDir}/copilot-quota-pill.png` });
  });

  test('Copilot pill renders even with a non-5H/WK window (Monthly)', async ({ page }) => {
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

    const copilot = page.getByTestId('hquota-copilot-primary');
    await expect(copilot).toBeVisible();
    // The single Monthly window becomes the primary; the strip never
    // goes blank just because the window isn't a 5H / WK bucket.
    await expect(copilot.locator('.hquota__value')).toContainText('18%');
    await expect(copilot.locator('.hquota__tag')).toContainText('MO');
  });

  test('all three CLIs render exactly one uniform pill each', async ({ page }) => {
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

    for (const cli of ['claude', 'codex', 'copilot']) {
      await expect(page.getByTestId(`hquota-${cli}-primary`)).toHaveCount(1);
    }
  });

  test('tone reflects the constraining window (Codex 72% -> warn)', async ({ page }) => {
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

    // Codex primary is the 72% session window -> warn tone.
    await expect(page.getByTestId('hquota-codex-primary')).toHaveAttribute('data-tone', 'warn');
    // Claude primary is the 55% weekly window -> ok tone.
    await expect(page.getByTestId('hquota-claude-primary')).toHaveAttribute('data-tone', 'ok');
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
