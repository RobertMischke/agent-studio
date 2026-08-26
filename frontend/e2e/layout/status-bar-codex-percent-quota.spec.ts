import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme, type Theme } from '../helpers/theme';

/**
 * Regression evidence for "Taskbar quota shows Codex missing although the
 * API delivers it (+ %-limit = 100%)" (2026-07-10).
 *
 * The live `/api/cli/quota` payload reports Codex windows as `unit: "%"`
 * with BOTH `used` and `limit` null and only `usedPct` set. This spec
 * mocks that exact shape and asserts:
 *  - the Codex card stays in the status-bar strip with one chip per window
 *    (a fresh, error-free snapshot never falls out of the row), and
 *  - opening the Codex modal shows the implied 100% cap in the Limit
 *    column instead of a bare "n/a" placeholder.
 *
 * Screenshots are captured as evidence (labelled --mocked because the
 * quota route is stubbed; the rest of the app runs against the live stack).
 */

const SHOT_DIR = process.env.CODEX_SHOT_DIR?.trim() || 'test-results';
const THEMES: readonly Theme[] = ['dark', 'light'];

function codexPercentQuotaReport() {
  return {
    at: new Date().toISOString(),
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'codex',
        fetchedAt: new Date().toISOString(),
        plan: 'Pro',
        windows: [
          { label: 'Current session (5h)', usedPct: 66, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '02:33' },
          { label: 'Weekly', usedPct: 12, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:33 on 3 May' },
          { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:25' },
          { label: 'Spark Weekly', usedPct: 4, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '16:25 on 14 Jun' },
        ],
        source: '/status',
        rawSample: null,
        error: null,
      },
    ],
  };
}

function failedCodexQuotaReport(retainLastGood: boolean) {
  const now = new Date().toISOString();
  return {
    at: now,
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'codex',
        fetchedAt: retainLastGood ? '2026-08-23T18:57:00Z' : now,
        cliVersion: retainLastGood ? 'codex-cli 0.149.0' : null,
        probeFailedAt: retainLastGood ? '2026-08-23T19:07:00Z' : null,
        plan: retainLastGood ? 'Pro' : null,
        windows: retainLastGood ? codexPercentQuotaReport().snapshots[0].windows : [],
        source: '/status',
        rawSample: null,
        error: retainLastGood
          ? 'Codex quota probe timed out while waiting for the /status panel.'
          : 'A task was canceled.',
      },
    ],
  };
}

test.describe('Status bar quota: Codex %-only payload', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.route('**/api/auth/status', route => route.fulfill({
      json: { profile: 'local', bootstrapRequired: false, authenticated: true, user: null },
    }));
    // Specific quota route first (first-registered route wins here) so the
    // Codex card renders our fixture regardless of the live stack.
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(codexPercentQuotaReport()),
      });
    });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
  });

  test('Codex card renders every %-only window in both themes and never drops out', async ({ page }) => {
    const card = page.getByTestId('hquota-card-codex');
    await expect(card).toBeVisible();

    // One chip per reported window; the %-only payload maps to real values,
    // not empty "—" placeholders.
    await expect(page.getByTestId('hquota-codex-5h')).toContainText('66%');
    await expect(page.getByTestId('hquota-codex-wk')).toContainText('12%');
    await expect(page.getByTestId('hquota-codex-spark-5h')).toContainText('0%');
    await expect(page.getByTestId('hquota-codex-spark-wk')).toContainText('4%');

    await card.scrollIntoViewIfNeeded();
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await card.screenshot({
        path: `${SHOT_DIR}/header-quota-strip-after--${theme}--playwright.png`,
      });
    }
  });

  test('Codex modal shows the implied 100% cap in both themes, not "n/a"', async ({ page }) => {
    // The backend-less worktree dev server can pop a startup "Failed to
    // load …" error dialog; let it settle and dismiss it so it does not
    // intercept the card click. (Against a live backend none of this fires.)
    await page.waitForTimeout(1500);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);

    const card = page.getByTestId('hquota-card-codex');
    await expect(card).toBeVisible();
    await card.scrollIntoViewIfNeeded();
    await card.click();

    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible({ timeout: 6_000 });

    const windowsList = page.getByTestId('cli-usage-modal-windows');
    await expect(windowsList).toBeVisible();
    // Each window card reads its implied 100% cap ("of 100%") for a
    // %-window instead of a bare "n/a".
    await expect(page.getByTestId('cli-usage-window').first()).toContainText('100%');
    // And no window falls back to the empty "n/a" placeholder.
    await expect(windowsList).not.toContainText('n/a');

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await modal.screenshot({
        path: `${SHOT_DIR}/cli-usage-modal-after--${theme}--playwright.png`,
      });
    }
  });

  test('failed probe degrades from a generic error to last-good stale values with version context', async ({ page }) => {
    await page.unroute('**/api/cli/quota');
    await page.route('**/api/cli/quota', route => route.fulfill({ json: failedCodexQuotaReport(false) }));
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);
    await page.keyboard.press('Escape');

    const card = page.getByTestId('hquota-card-codex');
    await expect(card).toHaveAttribute('data-state', 'error');
    await card.click();
    const beforeModal = page.getByTestId('cli-usage-modal-codex');
    await expect(beforeModal.getByText('A task was canceled.')).toBeVisible();
    await beforeModal.screenshot({ path: `${SHOT_DIR}/quota-probe-before--mocked.png` });

    await page.keyboard.press('Escape');
    await page.unroute('**/api/cli/quota');
    await page.route('**/api/cli/quota', route => route.fulfill({ json: failedCodexQuotaReport(true) }));
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1200);
    await page.keyboard.press('Escape');

    await expect(card).toHaveAttribute('data-state', 'stale');
    await expect(page.getByTestId('hquota-codex-5h')).toContainText('66%');
    const staleMarker = card.getByText(/^probe failed \d{2}:\d{2}, codex 0\.149\.0$/);
    await expect(staleMarker).toBeVisible();
    await staleMarker.hover();
    await expect(page.getByTestId('hquota-codex-probe-error-tooltip'))
      .toHaveText('Codex quota probe timed out while waiting for the /status panel.');

    await card.click();
    const afterModal = page.getByTestId('cli-usage-modal-codex');
    await expect(afterModal.getByTestId('cli-usage-probe-stale'))
      .toHaveText(/^probe failed \d{2}:\d{2}, codex 0\.149\.0$/);
    await expect(afterModal.getByText('66% used')).toBeVisible();
    await setTheme(page, 'light');
    await afterModal.screenshot({ path: `${SHOT_DIR}/quota-probe-after--mocked.png` });
    await setTheme(page, 'dark');
    await afterModal.screenshot({ path: `${SHOT_DIR}/quota-probe-after-dark--mocked.png` });
  });
});
