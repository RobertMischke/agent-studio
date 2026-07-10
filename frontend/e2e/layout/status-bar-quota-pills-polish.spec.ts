import { test, expect, type Page } from '@playwright/test';

/**
 * AGT-2058 — status-bar quota-pill polish (operator hotfix after the AGT-2030
 * usage revamp). Four asks, all visual:
 *   1. Plan badges ("MAX" / "PRO") removed from the strip — the plan lives in
 *      the per-CLI detail modal, not the bar.
 *   2. The coloured left accent bars on each card ("Seitenlinien") removed.
 *   3. Light-theme pill backgrounds lightened so the cards stop reading as dark
 *      foreign bodies in the light status bar.
 *   4. Cards + window chips share one height and baseline (grid-flush).
 *
 * The info content of the window chips (5H / WK tag + percent + trend bar) must
 * survive. This spec pins (1) via the DOM and keeps (4)'s info content honest,
 * and doubles as the screenshot harness for the before/after evidence written
 * into the task results/ folder (set STATUSBAR_SHOT_DIR + STATUSBAR_SHOT_STAGE).
 */

function mockQuotaReport() {
  const now = new Date().toISOString();
  return {
    at: now,
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'claude',
        fetchedAt: now,
        plan: 'Max',
        windows: [
          { label: '5-hour', usedPct: 38, used: null, limit: null, unit: '%', resetAt: null, resetLabel: 'in 1h 47m' },
          { label: 'Weekly', usedPct: 72, used: null, limit: null, unit: '%', resetAt: null, resetLabel: 'in 4d 3h' },
        ],
        source: 'mock',
        rawSample: null,
        error: null,
      },
      {
        cliType: 'codex',
        fetchedAt: now,
        plan: 'Pro',
        windows: [
          { label: 'Current session (5h)', usedPct: 66, used: null, limit: null, unit: '%', resetAt: null, resetLabel: 'in 2h 10m' },
          { label: 'Weekly', usedPct: 24, used: null, limit: null, unit: '%', resetAt: null, resetLabel: 'in 5d' },
        ],
        source: 'mock',
        rawSample: null,
        error: null,
      },
    ],
  };
}

async function loadStatusBar(page: Page) {
  // Only the quota endpoint drives the strip; mock it with the reproducer
  // payload above. Other /api calls are left alone (the always-mounted strip
  // reads nothing else), matching the existing status-bar quota specs.
  await page.route('**/api/cli/quota', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(mockQuotaReport()) });
  });

  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('hquota-claude-5h').locator('.hquota__value')).toHaveText('38%');
  await expect(page.getByTestId('hquota-codex-5h').locator('.hquota__value')).toHaveText('66%');
}

async function setTheme(page: Page, theme: 'dark' | 'light') {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
  }, theme);
  await page.waitForTimeout(250);
}

test.describe('AGT-2058 status-bar quota pills polish', () => {
  test('captures the status-bar quota strip in both themes', async ({ page }) => {
    await loadStatusBar(page);

    const dir = process.env.STATUSBAR_SHOT_DIR ?? 'test-results';
    const stage = process.env.STATUSBAR_SHOT_STAGE ?? 'shot';
    const statusBar = page.getByTestId('status-bar');
    const strip = page.locator('.statusbar__quota');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await statusBar.screenshot({ path: `${dir}/statusbar-${theme}--${stage}--mocked.png` });
      await strip.screenshot({ path: `${dir}/quota-strip-${theme}--${stage}--mocked.png` });
    }
  });

  test('drops the plan badge from the strip (plan lives in the detail modal)', async ({ page }) => {
    await loadStatusBar(page);
    // The MAX / PRO plan chips are gone from every card.
    await expect(page.locator('.hquota__plan')).toHaveCount(0);
    await expect(page.getByText('MAX', { exact: true })).toHaveCount(0);
    await expect(page.getByText('PRO', { exact: true })).toHaveCount(0);
  });

  test('uses a uniform border instead of a coloured left accent in both themes', async ({ page }) => {
    await loadStatusBar(page);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      const borders = await page.locator('.hquota__card').evaluateAll((cards) =>
        cards.map((card) => {
          const style = getComputedStyle(card);
          return {
            widths: [style.borderTopWidth, style.borderRightWidth, style.borderBottomWidth, style.borderLeftWidth],
            colors: [style.borderTopColor, style.borderRightColor, style.borderBottomColor, style.borderLeftColor],
          };
        }),
      );

      expect(borders.length, `[${theme}] rendered quota cards`).toBeGreaterThanOrEqual(2);
      for (const border of borders) {
        expect(new Set(border.widths).size, `[${theme}] uniform border widths`).toBe(1);
        expect(new Set(border.colors).size, `[${theme}] uniform border colours`).toBe(1);
      }
    }
  });

  test('keeps every window chip: 5H / WK tag + percent + trend bar', async ({ page }) => {
    await loadStatusBar(page);

    // Claude: 5H 38% (ok) and WK 72% (warn), both with a filled trend bar.
    const claude5h = page.getByTestId('hquota-claude-5h');
    const claudeWk = page.getByTestId('hquota-claude-wk');
    await expect(claude5h.locator('.hquota__tag')).toHaveText('5H');
    await expect(claude5h.locator('.hquota__value')).toHaveText('38%');
    await expect(claude5h.locator('.hquota__bar-fill')).toBeVisible();
    await expect(claudeWk.locator('.hquota__tag')).toHaveText('WK');
    await expect(claudeWk.locator('.hquota__value')).toHaveText('72%');
    await expect(claudeWk).toHaveAttribute('data-tone', 'warn');

    // Codex keeps both of its windows too.
    await expect(page.getByTestId('hquota-codex-5h').locator('.hquota__value')).toHaveText('66%');
    await expect(page.getByTestId('hquota-codex-wk').locator('.hquota__value')).toHaveText('24%');
  });

  test('cards and window chips share one height (grid-flush)', async ({ page }) => {
    await loadStatusBar(page);

    const cards = page.locator('.hquota__card');
    const count = await cards.count();
    expect(count).toBeGreaterThanOrEqual(2);

    // Every card is the same height and sits on the same top baseline.
    const boxes = await cards.evaluateAll((els) =>
      els.map((el) => {
        const r = el.getBoundingClientRect();
        return { top: Math.round(r.top), height: Math.round(r.height) };
      }),
    );
    const heights = new Set(boxes.map((b) => b.height));
    const tops = new Set(boxes.map((b) => b.top));
    expect(heights.size).toBe(1);
    expect(tops.size).toBe(1);

    // Within a card, all window chips share one height too.
    const chipHeights = await page
      .getByTestId('hquota-card-codex')
      .locator('.hquota__metric')
      .evaluateAll((els) => els.map((el) => Math.round(el.getBoundingClientRect().height)));
    expect(new Set(chipHeights).size).toBe(1);
  });
});
