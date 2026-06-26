import { test, expect } from '@playwright/test';

/**
 * Isolate every spec in this file from the live backend's stored client
 * defaults. `ClientDefaultsService.hydrate()` runs on app boot and pulls
 * `/api/clients/{id}/defaults` into localStorage; if the dev backend has a
 * saved profile (e.g. claude / claude-opus-4-8) it clobbers the localStorage
 * values these tests seed and the default-CLI / default-model assertions go
 * red for reasons unrelated to the picker. Returning an empty profile makes
 * the seeded localStorage authoritative and keeps the suite deterministic.
 */
test.beforeEach(async ({ page }) => {
  await page.route('**/api/clients/*/defaults', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ defaultCliType: null, defaultModel: null }),
    });
  });
});

/**
 * Visual & structural smoke for the slim header + bottom status bar shell.
 *
 * - The header should be short (well below the previous ~70px) so vertical
 *   space is reclaimed.
 * - The status bar must be present at the bottom and host the quick toggles
 *   (Usage / Orchestrator / Feed) and the unified default-CLI + default-model
 *   chip (see docs/frontend/audits/cli-model-selector-audit.md).
 * - The defaults popover should open above the bar (VS Code style) and
 *   persist the user's choice in localStorage.
 */
test.describe('Status bar and header size', () => {
  test('header is compact and status bar carries quota + defaults chip', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const header = page.locator('header.header');
    await expect(header).toBeVisible();
    const headerBox = await header.boundingBox();
    expect(headerBox, 'header box').not.toBeNull();
    expect(headerBox!.height).toBeLessThan(48);

    const statusBar = page.getByTestId('status-bar');
    await expect(statusBar).toBeVisible();
    const sbBox = await statusBar.boundingBox();
    expect(sbBox, 'status bar box').not.toBeNull();
    expect(sbBox!.height).toBeLessThan(40);

    // Quick toggles live in the status bar now.
    await expect(statusBar.getByTitle('CLI sessions')).toBeVisible();
    await expect(statusBar.getByTitle('Orchestrator chat')).toBeVisible();
    await expect(statusBar.getByTitle('Orchestrator feed')).toBeVisible();

    // Unified defaults chip.
    await expect(statusBar.getByTestId('status-bar-defaults')).toBeVisible();

    // Add Task remains the primary CTA in the header.
    await expect(header.getByRole('button', { name: /Add Task/ })).toBeVisible();

    await page.screenshot({
      path: 'test-results/status-bar-header.png',
      fullPage: false,
    });

    await statusBar.screenshot({
      path: 'test-results/status-bar-closeup.png',
    });

    await header.screenshot({
      path: 'test-results/header-closeup.png',
    });
  });

  test('focus / detail view layout still fits the new shell', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const firstCard = page.locator('app-job-card').first();
    if (await firstCard.count()) {
      await firstCard.click();
      await page.waitForTimeout(500);
      await page.screenshot({
        path: 'test-results/status-bar-focus-view.png',
        fullPage: false,
      });
    }
  });

  test('changing the default CLI through the unified chip persists in localStorage', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    await page.evaluate(() => localStorage.removeItem('defaultCliType'));
    await page.reload();
    await page.waitForTimeout(500);

    const chip = page.getByTestId('status-bar-defaults');
    // The unified chip renders the CLI as an emoji + the model name as text,
    // so the CLI label only appears in the aria-label, not the visible text.
    await expect(chip).toHaveAttribute('aria-label', /Copilot/i);

    await chip.click();
    const picker = page.getByTestId('status-bar-defaults-picker');
    await expect(picker).toBeVisible();

    await picker.getByTestId('status-bar-defaults-picker-cli-claude').click();
    // CLI change keeps the picker open; commit with Done.
    await picker.getByTestId('status-bar-defaults-picker-done').click();
    await expect(picker).not.toBeVisible();

    await expect(chip).toHaveAttribute('aria-label', /Claude Code/i);
    const stored = await page.evaluate(() => localStorage.getItem('defaultCliType'));
    expect(stored).toBe('claude');

    await page.screenshot({
      path: 'test-results/status-bar-cli-picked.png',
      fullPage: false,
    });
  });
});

/**
 * Default model picker (now inside the unified defaults chip) must remain
 * interactive regardless of catalog state: the chip is always clickable,
 * the popover opens with an empty hint when the catalog returns no models,
 * and the (CLI default) row stays selectable so the user can clear a stale
 * persisted default.
 */
test.describe('Status bar defaults picker - model section', () => {
  test('opens and persists a selection when catalog has models', async ({ page }) => {
    let catalogCalls = 0;
    await page.route('**/api/cli/*/models*', async (route) => {
      catalogCalls += 1;
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          models: [
            { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', isDefault: true, multiplier: 1 },
            { id: 'claude-opus-4-7', label: 'Opus 4.7', isDefault: false, multiplier: 5 },
          ],
          defaultModel: 'claude-sonnet-4-6',
          source: 'mock',
        }),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      localStorage.removeItem('defaultCliType');
      localStorage.removeItem('defaultModel:copilot');
      localStorage.removeItem('defaultModel:claude');
    });
    await page.reload();
    await page.waitForTimeout(500);

    const chip = page.getByTestId('status-bar-defaults');
    await expect(chip).toBeVisible();
    await expect(chip).toBeEnabled();
    await expect(chip).toContainText(/No model|CLI default/i);

    await chip.click();
    const picker = page.getByTestId('status-bar-defaults-picker');
    await expect(picker).toBeVisible();

    // Model click without a CLI change auto-commits + closes the picker.
    await picker.getByTestId('status-bar-defaults-picker-model-claude-opus-4-7').click();
    await expect(picker).not.toBeVisible();
    await expect(chip).toContainText(/opus 4\.7/i);

    const stored = await page.evaluate(() =>
      localStorage.getItem('defaultModel:' + (localStorage.getItem('defaultCliType') ?? 'copilot'))
    );
    expect(stored).toBe('claude-opus-4-7');
    expect(catalogCalls).toBeGreaterThan(0);

    await page.screenshot({
      path: 'test-results/status-bar-model-picked.png',
      fullPage: false,
    });
  });

  test('popover stays reachable when catalog returns empty so the default can be reset', async ({ page }) => {
    await page.route('**/api/cli/*/models*', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ models: [], defaultModel: null, source: 'mock' }),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      localStorage.setItem('defaultCliType', 'copilot');
      localStorage.setItem('defaultModel:copilot', 'gpt-4o-stale');
    });
    await page.reload();
    await page.waitForTimeout(500);

    const chip = page.getByTestId('status-bar-defaults');
    await expect(chip).toBeVisible();
    await expect(chip).toBeEnabled();
    await expect(chip).toContainText(/gpt-4o-stale/);

    await chip.click();
    const picker = page.getByTestId('status-bar-defaults-picker');
    await expect(picker).toBeVisible();
    await expect(picker.getByTestId('status-bar-defaults-picker-empty')).toBeVisible();

    // Selecting (CLI default) clears the persisted override.
    await picker.getByTestId('status-bar-defaults-picker-model-default').click();
    await expect(picker).not.toBeVisible();
    await expect(chip).toContainText(/CLI default|No model/i);

    const stored = await page.evaluate(() => localStorage.getItem('defaultModel:copilot'));
    expect(stored).toBeNull();

    await page.screenshot({
      path: 'test-results/status-bar-model-empty-catalog.png',
      fullPage: false,
    });
  });

  test('Refresh button re-fetches the catalog when it had failed', async ({ page }) => {
    let calls = 0;
    await page.route('**/api/cli/*/models*', async (route) => {
      calls += 1;
      if (calls === 1) {
        await route.fulfill({
          status: 503,
          contentType: 'application/json',
          body: JSON.stringify({ error: 'pty probe failed' }),
        });
        return;
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          models: [{ id: 'gpt-4o', label: 'GPT-4o', isDefault: true, multiplier: 1 }],
          defaultModel: 'gpt-4o',
          source: 'mock',
        }),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const chip = page.getByTestId('status-bar-defaults');
    await chip.click();
    const picker = page.getByTestId('status-bar-defaults-picker');
    await expect(picker).toBeVisible();
    // Initial render: the first ngOnInit fetch failed silently, so the
    // popover shows the empty-state hint. The "Refresh" button bypasses
    // the TTL and triggers a fresh fetch.
    await picker.getByTestId('status-bar-defaults-picker-refresh').click();
    await expect(picker.getByTestId('status-bar-defaults-picker-model-gpt-4o')).toBeVisible();
    expect(calls).toBeGreaterThanOrEqual(2);
  });
});
