import { test, expect } from '@playwright/test';
import { getCliUsage } from '../helpers/quota';

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

  test('quota strip renders without NG0100 (dev-mode change-detection error)', async ({ page }) => {
    // Regression guard for an ExpressionChangedAfterItHasBeenCheckedError
    // in QuotaStripComponent.resetText(): using Date.now() instead of the
    // ticking signal made the value drift across change-detection passes
    // when the wall clock crossed a minute boundary.
    const ngErrors: string[] = [];
    page.on('pageerror', err => {
      if (err.message.includes('NG0100')) ngErrors.push(err.message);
    });
    page.on('console', msg => {
      const txt = msg.text();
      if (msg.type() === 'error' && txt.includes('NG0100')) ngErrors.push(txt);
    });

    await page.goto('/');
    await page.getByRole('button', { name: /usage|cli sessions/i }).first().click();
    const sheet = page.locator('aside.sheet');
    await expect(sheet).toBeVisible();
    // Wait long enough to cross the 1s tick boundary a few times.
    await page.waitForTimeout(2500);

    expect(ngErrors, `NG0100 errors leaked:\n${ngErrors.join('\n')}`).toHaveLength(0);
  });
});
