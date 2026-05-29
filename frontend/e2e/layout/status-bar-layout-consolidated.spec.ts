import { test, expect } from '@playwright/test';

/**
 * F50 follow-up — Status Bar layout consolidation
 *
 * Verifies the visual contract operator filed on 2026-05-28:
 *   A. All bar items share one vertical center (no "icons rutsch nach
 *      unten" on the left side).
 *   B. Right cluster anchors to the right edge with internal centering.
 *   C. CLI quota pills (Claude / Codex / Copilot) use the same height,
 *      padding and radius - no card looks louder than its neighbours
 *      for chrome reasons.
 *   D. Card highlight has an explicit, hoverable explanation: tooltip
 *      names the semantic state (idle/warn/hot/stale/unavailable/error)
 *      so a "warum ist Codex gehighlighted?" question is answerable
 *      without reading code.
 *
 * The spec mocks /api/cli/quota so it produces a deterministic
 * "Codex at 72%" warn state without depending on real CLI sessions.
 */

function mockQuotaReport() {
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
        windows: [
          { label: 'Weekly premium', usedPct: 18, used: 90, limit: 500, unit: 'requests', resetAt: null, resetLabel: 'in 6d' },
        ],
        source: 'mock',
        rawSample: null,
        error: null,
      },
    ],
  };
}

test.describe('Status bar layout consolidated', () => {
  test.beforeEach(async ({ page }) => {
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
    await page.waitForTimeout(1000);
  });

  test('A. all bar items share a vertical center line', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    await expect(statusBar).toBeVisible();
    const barBox = await statusBar.boundingBox();
    expect(barBox).not.toBeNull();
    const barCenter = barBox!.y + barBox!.height / 2;

    // Sample left-side items: running chip, auto chip, three CLI quota cards.
    const samples = [
      statusBar.locator('.statusbar__group--left app-statusbar-item').nth(0),
      statusBar.locator('.statusbar__group--left app-statusbar-item').nth(1),
      statusBar.getByTestId('hquota-card-claude'),
      statusBar.getByTestId('hquota-card-codex'),
      statusBar.getByTestId('hquota-card-copilot'),
      // Right side: action buttons and unified defaults chip
      // (formerly two separate cli/model pickers, see
      // docs/cli-model-selector-audit.md).
      statusBar.getByTestId('orch-side-sheet-toggle'),
      statusBar.getByTestId('status-bar-defaults'),
    ];

    const centers: number[] = [];
    for (const loc of samples) {
      await expect(loc).toBeVisible();
      const box = await loc.boundingBox();
      expect(box).not.toBeNull();
      centers.push(box!.y + box!.height / 2);
    }

    // All centers within 2 px of the bar's center line.
    for (const c of centers) {
      expect(Math.abs(c - barCenter)).toBeLessThanOrEqual(2);
    }
  });

  test('C. CLI quota pills share the same height', async ({ page }) => {
    const cards = ['claude', 'codex', 'copilot'].map((cli) =>
      page.getByTestId(`hquota-card-${cli}`)
    );
    const heights: number[] = [];
    for (const c of cards) {
      await expect(c).toBeVisible();
      const box = await c.boundingBox();
      expect(box).not.toBeNull();
      heights.push(box!.height);
    }
    // All three pills within 1 px of each other.
    const min = Math.min(...heights);
    const max = Math.max(...heights);
    expect(max - min).toBeLessThanOrEqual(1);

    // And the height fits within the bar (no overflow that previously
    // pulled the bar to 28 px while chips stayed at 17 px).
    const statusBar = page.getByTestId('status-bar');
    const barBox = await statusBar.boundingBox();
    expect(heights[0]).toBeLessThanOrEqual(barBox!.height);
  });

  test('B. right cluster anchors to the viewport edge and gaps the left cluster', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    const left = statusBar.locator('.statusbar__group--left');
    const right = statusBar.locator('.statusbar__group--right');
    const leftBox = await left.boundingBox();
    const rightBox = await right.boundingBox();
    expect(leftBox).not.toBeNull();
    expect(rightBox).not.toBeNull();

    // Spacer between clusters.
    const gap = rightBox!.x - (leftBox!.x + leftBox!.width);
    expect(gap).toBeGreaterThan(20);

    // Right cluster reaches the right edge (within padding).
    expect(rightBox!.x + rightBox!.width).toBeGreaterThan(1580);
  });

  test('D. Codex pill carries a semantic-state tooltip', async ({ page }) => {
    const codexCard = page.getByTestId('hquota-card-codex');
    await expect(codexCard).toBeVisible();
    // Codex 5h at 72% drives warn tone, which feeds the explicit state.
    await expect(codexCard).toHaveAttribute('data-tone', 'warn');
    await expect(codexCard).toHaveAttribute('data-state', 'warn');

    // Hovering must surface the project's HTML tooltip with state words.
    await codexCard.hover();
    await page.waitForTimeout(200);
    const tooltip = page.getByTestId('app-tooltip');
    await expect(tooltip).toBeVisible();
    const tooltipText = (await tooltip.textContent()) ?? '';
    expect(tooltipText.toLowerCase()).toContain('quota warning');
    expect(tooltipText).toContain('Codex');
  });

  test('Claude (under 70%) is idle, Codex (72%) is warn', async ({ page }) => {
    await expect(page.getByTestId('hquota-card-claude')).toHaveAttribute('data-state', 'idle');
    await expect(page.getByTestId('hquota-card-codex')).toHaveAttribute('data-state', 'warn');
    await expect(page.getByTestId('hquota-card-copilot')).toHaveAttribute('data-state', 'idle');
  });

  test('full status bar screenshot (light + dark)', async ({ page }) => {
    const statusBar = page.getByTestId('status-bar');
    await statusBar.screenshot({ path: 'test-results/status-bar-consolidated-dark.png' });

    // Flip theme via the data-attribute the app reads. The toggle button
    // isn't reliably present in every shell; setting the attribute is the
    // same path the toggle uses internally.
    await page.evaluate(() => {
      document.documentElement.setAttribute('data-studio-theme', 'light');
    });
    await page.waitForTimeout(200);
    await statusBar.screenshot({ path: 'test-results/status-bar-consolidated-light.png' });
  });
});
