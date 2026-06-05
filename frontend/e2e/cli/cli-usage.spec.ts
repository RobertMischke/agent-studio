import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { getCliUsage } from '../helpers/quota';

/**
 * CLI-usage entry path after the loose `<app-cli-usage-sheet>` sidesheet
 * was retired (its quota glance + per-CLI session inventory folded into
 * the Workspace-settings home's CLI-Management section). The status-bar
 * "Usage" button now opens that single hub instead of a parallel modal.
 *
 * This spec verifies:
 *  - the backend `/api/cli/usage` report is sane (Copilot / Claude / Codex)
 *  - clicking `status-bar-usage` opens the home at the CLI-Management
 *    ("caps") section (`cli-admin-overlay`)
 *  - the embedded `cli-admin-sessions` section renders the
 *    `cli-sessions-panel` with the available CLI labels and no error
 *
 * The session list is the preserved feature — it must still render from
 * inside the home, reachable via the (now non-parallel) Usage trigger.
 */

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.CLI_USAGE_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'cli-usage');
})();

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test.describe('CLI usage hub (status-bar → settings home)', () => {
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

  test('status-bar Usage opens the home CLI-Management with the sessions panel', async ({ page }) => {
    await page.goto('/');

    // The Usage button is the only CLI-usage entry point now; it opens the
    // global Workspace-settings home at the "caps" (CLI Management) section.
    await page.getByTestId('status-bar-usage').click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();
    await expect(page.getByTestId('cli-admin-panel')).toBeVisible();

    // The preserved per-CLI per-project session inventory is embedded here.
    const sessionsSection = page.getByTestId('cli-admin-sessions');
    await expect(sessionsSection).toBeVisible();
    const sessions = page.getByTestId('cli-sessions-panel');
    await expect(sessions).toBeVisible();

    // The panel lazy-loads `/api/cli/usage`; wait out the loading state.
    await expect(sessions.getByText('Loading native CLI session stores...')).toHaveCount(0, {
      timeout: 15_000,
    });

    // Available CLI section headers render even with zero sessions.
    for (const label of ['Copilot', 'Claude Code', 'Codex']) {
      await expect(sessions.getByText(label, { exact: true }).first()).toBeVisible();
    }

    // No error surfaced from the session load.
    await expect(sessions.locator('.sessions__error')).toHaveCount(0);

    await page.getByTestId('cli-admin-overlay').screenshot({
      path: path.join(SCREENSHOT_DIR, '01-cli-management-top.png'),
    });

    // Scroll the preserved session inventory's header into view and capture
    // the viewport for visual evidence that the feature still renders from
    // inside the home. (A full-element shot would be tens of thousands of px
    // tall on a machine with many real sessions.)
    await sessionsSection.scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-cli-sessions-panel.png') });
  });

  test('the status-bar Usage button reflects the home open state', async ({ page }) => {
    await page.goto('/');

    const usageBtn = page.getByTestId('status-bar-usage');
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'false');

    await usageBtn.click();
    await expect(page.getByTestId('cli-admin-overlay')).toBeVisible();
    // The trigger reflects the home being open (no parallel modal of its own).
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'true');

    // The home is a full-screen modal; it closes via its own close control,
    // after which the Usage trigger goes inactive again — no orphaned overlay.
    await page.getByTestId('workspace-settings-close').click();
    await expect(page.getByTestId('cli-admin-overlay')).not.toBeVisible();
    await expect(page.getByTestId('workspace-settings-overlay')).not.toBeVisible();
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'false');
  });
});
