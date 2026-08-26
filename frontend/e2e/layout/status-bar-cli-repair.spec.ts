import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme, type Theme } from '../helpers/theme';

const SCREENSHOT_DIR = process.env.CLI_REPAIR_RESULTS_DIR?.trim() || 'test-results';
const THEMES: readonly Theme[] = ['light', 'dark'];

test.describe('Status bar local CLI repair note', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await page.route('**/api/auth/status', route => route.fulfill({
      json: { profile: 'local', bootstrapRequired: false, authenticated: true, user: null },
    }));
    await page.route('**/api/cli/repair-status', route => route.fulfill({
      json: {
        at: '2026-08-18T10:06:00Z',
        repairs: [{
          cliType: 'claude',
          status: 'repaired',
          attemptedAt: '2026-08-18T10:04:00Z',
          completedAt: '2026-08-18T10:05:00Z',
          versionBefore: '2.1.231',
          versionAfter: '2.1.234',
          note: 'CLI repaired at 2026-08-18T10:05:00Z',
          detail: 'npm global reinstall restored claude.cmd; 2.1.231 -> 2.1.234.',
        }],
      },
    }));
    await page.route('**/api/clients', route => route.fulfill({ json: [] }));
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: [] }));
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
  });

  test('shows a quiet successful repair note in both themes', async ({ page }) => {
    const note = page.getByTestId('status-bar-cli-repair');
    await expect(note).toBeVisible();
    await expect(note).toContainText(/CLI repaired at \d{2}:\d{2}/);
    await expect(note).toHaveAttribute('data-signal-tone', 'calm');

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await page.getByTestId('status-bar').screenshot({
        path: `${SCREENSHOT_DIR}/status-bar-cli-repaired--${theme}--mocked.png`,
      });
    }
  });

  test('uses warning treatment only when repair failed', async ({ page }) => {
    await page.unroute('**/api/cli/repair-status');
    await page.route('**/api/cli/repair-status', route => route.fulfill({
      json: {
        at: '2026-08-18T11:06:00Z',
        repairs: [{
          cliType: 'claude',
          status: 'failed',
          attemptedAt: '2026-08-18T11:04:00Z',
          completedAt: '2026-08-18T11:05:00Z',
          versionBefore: '2.1.234',
          versionAfter: null,
          note: 'CLI repair failed at 2026-08-18T11:05:00Z',
          detail: 'npm install exited 1.',
        }],
      },
    }));
    await page.reload();

    const note = page.getByTestId('status-bar-cli-repair');
    await expect(note).toContainText(/CLI repair failed at \d{2}:\d{2}/);
    await expect(note).toHaveAttribute('data-signal-tone', 'mismatch');
  });
});
