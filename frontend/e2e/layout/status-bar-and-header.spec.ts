import { test, expect } from '@playwright/test';

/**
 * Visual & structural smoke for the slim header + bottom status bar shell.
 *
 * - The header should be short (well below the previous ~70px) so vertical
 *   space is reclaimed.
 * - The status bar must be present at the bottom and host the quick toggles
 *   (Usage / Orchestrator / Feed) and the default-CLI / default-model
 *   pickers.
 * - The picker popups should open above the bar (VS Code style) and persist
 *   the user's choice in localStorage.
 */
test.describe('Status bar and header size', () => {
  test('header is compact and status bar carries quota + pickers', async ({ page }) => {
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

    // Add Task remains the primary CTA in the header.
    await expect(header.getByRole('button', { name: /Add Task/ })).toBeVisible();

    await page.screenshot({
      path: 'test-results/status-bar-header.png',
      fullPage: false,
    });

    // Closeup of just the status bar so the picker labels read clearly.
    await statusBar.screenshot({
      path: 'test-results/status-bar-closeup.png',
    });

    // Closeup of just the header so the slim brand + tabs read clearly.
    await header.screenshot({
      path: 'test-results/header-closeup.png',
    });
  });

  test('focus / detail view layout still fits the new shell', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    // Open the first card in any column to enter focus view.
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

  test('default CLI picker persists selection', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('http://localhost:4010');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    // Reset any previous run's state.
    await page.evaluate(() => localStorage.removeItem('defaultCliType'));
    await page.reload();
    await page.waitForTimeout(500);

    const cliPicker = page.getByTestId('status-bar-cli-picker');
    await expect(cliPicker).toContainText('Copilot');

    await cliPicker.click();
    await page.getByRole('button', { name: /Claude Code/ }).click();
    await expect(cliPicker).toContainText('Claude Code');

    const stored = await page.evaluate(() => localStorage.getItem('defaultCliType'));
    expect(stored).toBe('claude');

    await page.screenshot({
      path: 'test-results/status-bar-cli-picked.png',
      fullPage: false,
    });
  });
});

/**
 * Default model picker — must remain interactive regardless of catalog state.
 *
 * Before this fix the picker button was disabled whenever the model catalog
 * came back empty (no Copilot session, PTY probe 503, etc.), which left the
 * user with no way to change or clear the persisted default. The tests below
 * mock the catalog endpoint and assert the menu is always reachable.
 */
test.describe('Status bar default-model picker', () => {
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

    const modelPicker = page.getByTestId('status-bar-model-picker');
    await expect(modelPicker).toBeVisible();
    await expect(modelPicker).toBeEnabled();
    await expect(modelPicker).toContainText('CLI default');

    await modelPicker.click();
    const menu = page.locator('.statusbar__menu', { hasText: 'Default model' });
    await expect(menu).toBeVisible();

    await menu.getByRole('button', { name: /Opus 4\.7/ }).click();
    await expect(modelPicker).toContainText('Opus 4.7');

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

  test('menu is reachable when catalog returns empty so the default can be reset', async ({ page }) => {
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
    // Seed a stale persisted model so the reset path has something to clear.
    await page.evaluate(() => {
      localStorage.setItem('defaultCliType', 'copilot');
      localStorage.setItem('defaultModel:copilot', 'gpt-4o-stale');
    });
    await page.reload();
    await page.waitForTimeout(500);

    const modelPicker = page.getByTestId('status-bar-model-picker');
    await expect(modelPicker).toBeVisible();
    // The regression: button was disabled here. It must stay enabled.
    await expect(modelPicker).toBeEnabled();
    await expect(modelPicker).toContainText('gpt-4o-stale');

    await modelPicker.click();
    const menu = page.locator('.statusbar__menu', { hasText: 'Default model' });
    await expect(menu).toBeVisible();
    await expect(menu).toContainText(/No models reported|Catalog unavailable/);
    await expect(menu.getByTestId('status-bar-model-refresh')).toBeVisible();

    await menu.getByTestId('status-bar-model-default').click();
    await expect(modelPicker).toContainText('CLI default');

    const stored = await page.evaluate(() => localStorage.getItem('defaultModel:copilot'));
    expect(stored).toBeNull();

    await page.screenshot({
      path: 'test-results/status-bar-model-empty-catalog.png',
      fullPage: false,
    });
  });

  test('refresh action re-fetches the catalog when it had failed', async ({ page }) => {
    // Fresh `page` fixture means a fresh storage context — register the route
    // before the first navigation so the initial ngOnInit fetch hits the 503
    // branch, then the refresh click hits the 200 branch.
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

    const modelPicker = page.getByTestId('status-bar-model-picker');
    await modelPicker.click();
    const menu = page.locator('.statusbar__menu', { hasText: 'Default model' });
    await expect(menu).toContainText('Catalog unavailable');

    await menu.getByTestId('status-bar-model-refresh').click();
    // Catalog now resolves with one model; menu should re-render with it.
    await expect(menu.getByRole('button', { name: /GPT-4o/ })).toBeVisible();
    expect(calls).toBeGreaterThanOrEqual(2);
  });
});
