import { test, expect } from '@playwright/test';

/**
 * Settings panel — Appearance (Theme) + Layout (Activity bar) segmented
 * toggles (ASS-712 shared SegmentedControl).
 *
 * Pins the polish contract the cards were filed for:
 *   1. Each toggle is a labelled `role="group"` of buttons (not two glued
 *      words), with exactly one option carrying `aria-pressed="true"`.
 *   2. Clicking the inactive option flips the selection AND drives the real
 *      side effect — the Theme switch flips `<html data-studio-theme>`.
 *   3. The Activity bar switch flips its pressed option the same way.
 *
 * Theme + activity-bar side persist to localStorage only, and Playwright
 * gives each test a fresh context, so no explicit cleanup is required.
 */
test.describe('Settings — Appearance/Layout segmented toggles', () => {
  test('Theme toggle: labelled group, single pressed option, flips the document theme', async ({ page }) => {
    await page.addInitScript(() => {
      try { localStorage.setItem('atp.studio.theme', 'light'); } catch { /* ignore */ }
    });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.getByTestId('studio-ab-settings').click();

    const themeGroup = page.getByRole('group', { name: 'Theme' });
    await expect(themeGroup).toBeVisible({ timeout: 10_000 });

    const dark = page.getByTestId('settings-theme-dark');
    const light = page.getByTestId('settings-theme-light');

    // Light is active on load.
    await expect(light).toHaveAttribute('aria-pressed', 'true');
    await expect(dark).toHaveAttribute('aria-pressed', 'false');
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');

    // Switching to Dark flips both the pressed state and the document theme.
    await dark.click();
    await expect(dark).toHaveAttribute('aria-pressed', 'true');
    await expect(light).toHaveAttribute('aria-pressed', 'false');
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  });

  test('Activity bar toggle: labelled group, single pressed option, flips on click', async ({ page }) => {
    await page.addInitScript(() => {
      try { localStorage.setItem('atp.studio.activityBarSide', 'left'); } catch { /* ignore */ }
    });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.getByTestId('studio-ab-settings').click();

    const abGroup = page.getByRole('group', { name: 'Activity bar' });
    await expect(abGroup).toBeVisible({ timeout: 10_000 });

    const left = page.getByTestId('settings-activitybar-left');
    const right = page.getByTestId('settings-activitybar-right');

    await expect(left).toHaveAttribute('aria-pressed', 'true');
    await expect(right).toHaveAttribute('aria-pressed', 'false');

    await right.click();
    await expect(right).toHaveAttribute('aria-pressed', 'true');
    await expect(left).toHaveAttribute('aria-pressed', 'false');
  });
});
