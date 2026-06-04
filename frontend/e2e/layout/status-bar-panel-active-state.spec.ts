import { test, expect, Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * ASS-693: status-bar panel-trigger buttons must show a pressed/active
 * state bound to the open flag of the panel they toggle, and clicking an
 * active button closes the panel (toggle). The active state is exposed via
 * `aria-pressed` + the `statusbar__item--active` class.
 *
 * Isolate from the live backend's stored client defaults so app boot
 * doesn't clobber state (mirrors status-bar-and-header.spec.ts).
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

async function gotoBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(800);
}

/**
 * A panel trigger starts un-pressed, becomes pressed when clicked open, and
 * returns to un-pressed when clicked again (toggle). Asserted via
 * aria-pressed so it doubles as an accessibility check.
 */
async function expectTogglesActive(page: Page, testid: string): Promise<void> {
  const statusBar = page.getByTestId('status-bar');
  const button = statusBar.getByTestId(testid);
  await expect(button, `${testid} visible`).toBeVisible();
  await expect(button, `${testid} starts un-pressed`).toHaveAttribute('aria-pressed', 'false');

  await button.click();
  await expect(button, `${testid} pressed after open`).toHaveAttribute('aria-pressed', 'true');

  await button.click();
  await expect(button, `${testid} un-pressed after toggle close`).toHaveAttribute(
    'aria-pressed',
    'false',
  );
}

test.describe('Status bar panel buttons - active/toggle state', () => {
  test('Usage button reflects + toggles its open state', async ({ page }) => {
    await gotoBoard(page);
    await expectTogglesActive(page, 'status-bar-usage');
  });

  test('Orchestrator button reflects + toggles its open state', async ({ page }) => {
    await gotoBoard(page);
    await expectTogglesActive(page, 'orch-side-sheet-toggle');
  });

  test('Settings button reflects its open state', async ({ page }) => {
    await gotoBoard(page);
    const statusBar = page.getByTestId('status-bar');
    const settings = statusBar.getByTestId('status-bar-settings');
    await expect(settings).toHaveAttribute('aria-pressed', 'false');

    await settings.click();
    await expect(settings).toHaveAttribute('aria-pressed', 'true');

    // The workspace-settings home is a full-screen modal whose backdrop
    // covers the status bar, so it closes via its own ✕ rather than by
    // re-clicking the (now-occluded) trigger. The active state must clear
    // once the panel's open flag flips back.
    await page.getByTestId('workspace-settings-close').click();
    await expect(settings).toHaveAttribute('aria-pressed', 'false');
  });

  test('each button tracks its own panel independently', async ({ page }) => {
    await gotoBoard(page);
    // Force dark explicitly — a fresh context defaults to light, so without
    // this the "dark" evidence screenshot would actually render in light.
    await setTheme(page, 'dark');
    const statusBar = page.getByTestId('status-bar');
    const usage = statusBar.getByTestId('status-bar-usage');
    const settings = statusBar.getByTestId('status-bar-settings');

    await usage.click();
    await expect(usage).toHaveAttribute('aria-pressed', 'true');
    await expect(settings).toHaveAttribute('aria-pressed', 'false');

    // Active-state evidence: capture the bar with one button pressed.
    await statusBar.screenshot({
      path: 'test-results/status-bar-active-dark.png',
    });

    await settings.click();
    await expect(settings).toHaveAttribute('aria-pressed', 'true');
    // Usage stays open (independent overlays); this asserts the active
    // state tracks each panel's own flag rather than a single shared one.
    await expect(usage).toHaveAttribute('aria-pressed', 'true');
  });

  test('active state renders in light theme', async ({ page }) => {
    await gotoBoard(page);
    // setTheme stamps the attribute AND persists to localStorage; otherwise
    // the shell's theme effect reverts it on the next change-detection.
    await setTheme(page, 'light');
    const statusBar = page.getByTestId('status-bar');
    const usage = statusBar.getByTestId('status-bar-usage');
    await usage.click();
    await expect(usage).toHaveAttribute('aria-pressed', 'true');
    await statusBar.screenshot({
      path: 'test-results/status-bar-active-light.png',
    });
  });
});
