import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme } from './helpers/theme';

/**
 * AGT-2604: Screenshot evidence for "Recorded model usage" telemetry range.
 * Captures before (no range) and after (range visible) states.
 */

const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim() || 'test-results';

/** Dismiss any overlay (error dialog, crash-recovery prompt) that could block clicks. */
async function dismissOverlays(page: import('@playwright/test').Page) {
  const overlaySelectors = [
    '[data-testid="error-dialog-overlay"]',
    '[data-testid="crash-recovery-prompt-overlay"]',
  ];
  for (const sel of overlaySelectors) {
    const el = page.locator(sel);
    if (await el.isVisible().catch(() => false)) {
      await page.keyboard.press('Escape').catch(() => {});
      await page.waitForTimeout(300);
    }
  }
}

test.describe('AGT-2604 telemetry-range screenshots', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(RESULTS_DIR, { recursive: true });
    await page.setViewportSize({ width: 1400, height: 900 });
    // Suppress crash-recovery-prompt before loading so it never blocks clicks.
    await page.route('**/api/crash-recovery/pending', route =>
      route.fulfill({ json: { pending: [] } }));
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);
  });

  test('before: scope note without date range (no firstActivity/lastActivity)', async ({ page }) => {
    await page.route('**/api/cli/quota**', route => {
      if (route.request().method() !== 'GET') return route.continue();
      return route.fulfill({
        json: {
          at: new Date().toISOString(),
          ttlSeconds: 600,
          snapshots: [{
            cliType: 'claude',
            fetchedAt: new Date().toISOString(),
            plan: 'Max',
            source: '/status',
            error: null,
            windows: [
              { label: '5-hour', usedPct: 42, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '01:49 on 11 Aug' },
              { label: 'Weekly', usedPct: 18, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '20:49 on 17 Aug' },
            ],
          }],
        },
      });
    });
    await page.route('**/api/runner/token-summary-aggregate**', route => route.fulfill({
      json: {
        projects: 3, orchestratorEntries: 1501, orchestratorLlmCalls: 1501,
        totalInputTokens: 1_200_000_000, totalOutputTokens: 301_000_000,
        totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 7442, allModelsPriced: true,
        byModel: [
          { model: 'claude-sonnet-4-6', calls: 1200, inputTokens: 900_000_000, outputTokens: 220_000_000, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 5500, modelPriced: true },
          { model: 'claude-haiku-4-5', calls: 301, inputTokens: 300_000_000, outputTokens: 81_000_000, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 1942, modelPriced: true },
        ],
        byProject: [],
        fetchedAt: new Date().toISOString(),
        // No firstActivity / lastActivity — simulates telemetry without timestamps
        disclaimer: 'Theoretical API cost.',
      },
    }));
    await page.route('**/api/adhoc-usage/**', route => route.fulfill({
      json: {
        calls: 0, inputTokens: 0, outputTokens: 0,
        cacheReadTokens: 0, cacheCreationTokens: 0,
        estimatedApiCostUsd: 0, allModelsPriced: false,
        bySource: [], byDay: [], byModel: [],
        logPath: '(bus)', logSizeBytes: 0, logModifiedAt: null, disclaimer: '',
      },
    }));

    await page.reload();
    await page.waitForTimeout(1000);
    await dismissOverlays(page);

    const card = page.getByTestId('hquota-card-claude');
    await expect(card).toBeVisible({ timeout: 6_000 });
    await card.scrollIntoViewIfNeeded();
    await dismissOverlays(page);
    await card.click();

    const modal = page.getByTestId('cli-usage-modal-claude');
    await expect(modal).toBeVisible({ timeout: 4_000 });

    // Range badge must NOT appear when no timestamps are present.
    await expect(modal.locator('.cum__scope-range')).toHaveCount(0);

    await setTheme(page, 'dark');
    await modal.screenshot({ path: `${RESULTS_DIR}/agt2604-before-no-range-dark.png` });
    await setTheme(page, 'light');
    await modal.screenshot({ path: `${RESULTS_DIR}/agt2604-before-no-range-light.png` });
  });

  test('after: date range badge shows when firstActivity + lastActivity present', async ({ page }) => {
    await page.route('**/api/cli/quota**', route => {
      if (route.request().method() !== 'GET') return route.continue();
      return route.fulfill({
        json: {
          at: new Date().toISOString(),
          ttlSeconds: 600,
          snapshots: [{
            cliType: 'claude',
            fetchedAt: new Date().toISOString(),
            plan: 'Max',
            source: '/status',
            error: null,
            windows: [
              { label: '5-hour', usedPct: 42, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '01:49 on 11 Aug' },
              { label: 'Weekly', usedPct: 18, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '20:49 on 17 Aug' },
            ],
          }],
        },
      });
    });
    await page.route('**/api/runner/token-summary-aggregate**', route => route.fulfill({
      json: {
        projects: 3, orchestratorEntries: 1501, orchestratorLlmCalls: 1501,
        totalInputTokens: 1_200_000_000, totalOutputTokens: 301_000_000,
        totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 7442, allModelsPriced: true,
        byModel: [
          { model: 'claude-sonnet-4-6', calls: 1200, inputTokens: 900_000_000, outputTokens: 220_000_000, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 5500, modelPriced: true },
          { model: 'claude-haiku-4-5', calls: 301, inputTokens: 300_000_000, outputTokens: 81_000_000, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 1942, modelPriced: true },
        ],
        byProject: [],
        fetchedAt: new Date().toISOString(),
        firstActivity: '2025-08-11T09:13:42Z',
        lastActivity: '2026-08-11T19:58:01Z',
        disclaimer: 'Theoretical API cost.',
      },
    }));
    await page.route('**/api/adhoc-usage/**', route => route.fulfill({
      json: {
        calls: 45, inputTokens: 12_000, outputTokens: 3_000,
        cacheReadTokens: 0, cacheCreationTokens: 0,
        estimatedApiCostUsd: 0.05, allModelsPriced: true,
        bySource: [],
        byDay: [
          { date: '2025-08-11', calls: 5, inputTokens: 1000, outputTokens: 300, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.01 },
          { date: '2026-08-11', calls: 40, inputTokens: 11000, outputTokens: 2700, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.04 },
        ],
        byModel: [{ model: 'claude-haiku-4-5-20251001', calls: 45, inputTokens: 12_000, outputTokens: 3_000, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.05, modelPriced: true }],
        logPath: '(bus)', logSizeBytes: 8192, logModifiedAt: '2026-08-11T19:58:01Z', disclaimer: '',
      },
    }));

    await page.reload();
    await page.waitForTimeout(1000);
    await dismissOverlays(page);

    const card = page.getByTestId('hquota-card-claude');
    await expect(card).toBeVisible({ timeout: 6_000 });
    await card.scrollIntoViewIfNeeded();
    await dismissOverlays(page);
    await card.click();

    const modal = page.getByTestId('cli-usage-modal-claude');
    await expect(modal).toBeVisible({ timeout: 4_000 });

    // Range badge must appear and contain correct dates.
    const rangeSpan = modal.locator('.cum__scope-range');
    await expect(rangeSpan).toBeVisible();
    await expect(rangeSpan).toContainText('since 2025-08-11');
    await expect(rangeSpan).toContainText('as of 2026-08-11');

    await setTheme(page, 'dark');
    await modal.screenshot({ path: `${RESULTS_DIR}/agt2604-after-with-range-dark.png` });
    await setTheme(page, 'light');
    await modal.screenshot({ path: `${RESULTS_DIR}/agt2604-after-with-range-light.png` });
  });
});
