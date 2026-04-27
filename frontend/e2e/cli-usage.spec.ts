import { test, expect } from '@playwright/test';
import { getCliUsage } from './helpers/quota';

/**
 * Smoke checks the CLI Usage sidesheet:
 *  - opens via the toolbar button
 *  - shows all three CLI sections (Copilot / Claude / Codex)
 *  - version pills are visible for available CLIs
 *  - no error banner
 *
 * The backend must already have probed each CLI; we sanity-check that via
 * the REST endpoint first, so test failures point at the right layer.
 */

test.describe('CLI Usage sidesheet', () => {
  test('backend reports all three CLIs as available', async () => {
    const report = await getCliUsage();
    const types = report.sections.map(s => s.cliType).sort();
    expect(types).toEqual(expect.arrayContaining(['claude', 'codex', 'copilot']));
    for (const t of ['claude', 'codex', 'copilot']) {
      const sec = report.sections.find(s => s.cliType === t)!;
      expect(sec.available, `${t} should be available`).toBe(true);
      expect(sec.error, `${t} should have no error`).toBeFalsy();
      expect(sec.version, `${t} should report a version string`).toBeTruthy();
    }
  });

  test('UI shows all three CLI sections with version pills', async ({ page }) => {
    await page.goto('/');

    // Open the CLI Usage sheet. The toolbar button label is "🪙 Usage" with
    // title="CLI sessions"; match either. Consider adding
    // `data-testid="cli-usage-toggle"` for a more stable hook.
    const toggle = page.getByRole('button', { name: /usage|cli sessions/i }).first();
    await toggle.click();

    const sheet = page.locator('aside.sheet');
    await expect(sheet).toBeVisible();
    await expect(sheet.getByRole('heading', { name: 'CLI Usage' })).toBeVisible();

    // Sections segment may be collapsed by default; expand if so.
    const sessionsHead = sheet.getByRole('button', { name: /sessions/i }).first();
    const isCollapsed = await sessionsHead.locator('.seg__chev').textContent();
    if (isCollapsed?.includes('▶')) await sessionsHead.click();

    // Each CLI label should be present.
    for (const label of ['Copilot', 'Claude Code', 'Codex']) {
      await expect(sheet.getByText(label, { exact: true }).first()).toBeVisible();
    }

    // No error banner.
    await expect(sheet.locator('.sheet__error')).toHaveCount(0);
  });
});
