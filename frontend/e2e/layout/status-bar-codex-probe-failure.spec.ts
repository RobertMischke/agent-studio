import type { Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme, type Theme } from '../helpers/theme';
import { expect, test } from '../fixtures/dev-backend';

const RESULT_DIR = process.env.JOB_RESULTS_DIR?.trim() || 'test-results';
const THEMES: readonly Theme[] = ['light', 'dark'];
const FAILED_AT = '2026-08-23T19:07:00+02:00';
const PROBE_ERROR = 'Codex /status quota probe timed out or was canceled.';

function report(withLastGood: boolean) {
  return {
    at: '2026-08-23T19:07:01+02:00',
    ttlSeconds: 600,
    snapshots: [{
      cliType: 'codex',
      cliVersion: withLastGood ? 'codex-cli 0.149.0' : null,
      fetchedAt: withLastGood ? '2026-08-23T18:55:00+02:00' : FAILED_AT,
      probeFailedAt: withLastGood ? FAILED_AT : null,
      plan: withLastGood ? 'Pro' : null,
      source: '/status',
      rawSample: null,
      error: withLastGood ? PROBE_ERROR : 'A task was canceled.',
      windows: withLastGood ? [
        { label: 'Weekly', usedPct: 18, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '17:12 on 1 Sep' },
        { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '19:09' },
        { label: 'Spark Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '14:09 on 2 Sep' },
      ] : [],
    }],
  };
}

async function load(page: Page, withLastGood: boolean) {
  await page.route('**/api/auth/status', route => route.fulfill({
    json: {
      profile: 'networked',
      bootstrapRequired: false,
      authenticated: true,
      user: {
        id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false,
      },
    },
  }));
  await page.route('**/api/cli/quota', route => route.fulfill({ json: report(withLastGood) }));
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  await expect(page.getByTestId('hquota-card-codex')).toBeVisible();
  const startupError = page.getByTestId('error-dialog-overlay');
  if (await startupError.isVisible()) {
    await page.keyboard.press('Escape');
    await expect(startupError).toHaveCount(0);
  }
}

test.describe('Codex quota probe graceful degradation', () => {
  test.beforeEach(() => mkdirSync(RESULT_DIR, { recursive: true }));

  test('captures the canceled/error-only incident state', async ({ page, devBackend }) => {
    void devBackend;
    await load(page, false);
    await page.getByTestId('hquota-card-codex').click();
    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible();
    await expect(modal).toContainText('No quota window data.');
    // Recreate the old error branch for the before evidence. The mocked API
    // payload is the operator's incident response, while the current component
    // would otherwise apply the new stale presentation before the screenshot.
    const failure = page.getByTestId('cli-usage-probe-failed');
    await failure.evaluate(element => {
      element.className = 'cum__error';
      element.removeAttribute('data-testid');
      element.textContent = 'A task was canceled.';
    });
    await expect(modal.getByText('A task was canceled.', { exact: true })).toBeVisible();
    await modal.screenshot({
      path: `${RESULT_DIR}/quota-probe-failure-before--mocked.png`,
    });
  });

  test('keeps last-good values with a versioned stale marker and error tooltip', async ({ page, devBackend }) => {
    void devBackend;
    await load(page, true);

    const card = page.getByTestId('hquota-card-codex');
    await expect(page.getByTestId('hquota-codex-wk')).toContainText('18%');
    await expect(page.getByTestId('hquota-codex-spark-5h')).toContainText('0%');
    const staleMarker = page.getByTestId('hquota-probe-failed');
    await expect(staleMarker).toHaveText('probe failed 19:07, codex 0.149.0');
    await staleMarker.hover();
    await expect(page.getByText(PROBE_ERROR, { exact: true })).toBeVisible();

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await card.screenshot({
        path: `${RESULT_DIR}/quota-probe-failure-after-strip-${theme}--mocked.png`,
      });
    }

    await card.click();
    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible();
    await expect(page.getByTestId('cli-usage-probe-failed'))
      .toContainText('probe failed 19:07, codex 0.149.0');
    await expect(page.getByTestId('cli-usage-modal-windows').getByTestId('cli-usage-window'))
      .toHaveCount(3);

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await modal.screenshot({
        path: `${RESULT_DIR}/quota-probe-failure-after-modal-${theme}--mocked.png`,
      });
    }
  });
});
