import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { setTheme, type Theme } from '../helpers/theme';

/**
 * Status bar local CLI repair notice: quiet success and failure-only alarm.
 */

const THEMES: readonly Theme[] = ['dark', 'light'];
const SHOT_DIR = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : 'test-results';

function quotaReport(status: 'repaired' | 'failed') {
  return {
    at: '2026-08-26T15:30:00Z',
    ttlSeconds: 600,
    snapshots: [],
    latestCliRepair: {
      cliType: 'claude',
      status,
      completedAt: '2026-08-26T15:24:00Z',
      versionBefore: '2.1.231',
      versionAfter: status === 'repaired' ? '2.1.234' : '2.1.231',
      message: status === 'repaired'
        ? 'CLI repaired and verified with --version.'
        : 'CLI repair failed; operator attention is required.',
    },
  };
}

function json(body: unknown) {
  return (route: Route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function load(page: Page, status: 'repaired' | 'failed') {
  await page.route('**/api/auth/status', json({
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));
  await page.route('**/api/environment**', json({ isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/tasks/grouped', json({
    preparation: [], ready: [], progress: [], review: [], completed: [], archive: [],
  }));
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/v1/management/remote-hosts', json([]));
  await page.route('**/api/cli/quota', json(quotaReport(status)));
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  return page.getByTestId('status-bar-cli-repair');
}

test.describe('Status bar local CLI repair notice', () => {
  test.beforeEach(() => mkdirSync(SHOT_DIR, { recursive: true }));

  test('renders successful repair as a quiet status note in both themes', async ({ page }) => {
    const note = await load(page, 'repaired');

    await expect(note).toContainText('CLI repaired at');
    await expect(note).toHaveAttribute('role', 'status');
    await expect(note).not.toHaveAttribute('role', 'alert');
    await expect(note).toHaveAttribute('title', /Claude.*2\.1\.231 to 2\.1\.234/);

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await note.screenshot({ path: path.join(SHOT_DIR, `cli-repair-success-${theme}.png`) });
    }
  });

  test('uses alert semantics only when repair fails', async ({ page }) => {
    const note = await load(page, 'failed');

    await expect(note).toContainText('CLI repair failed at');
    await expect(note).toHaveAttribute('role', 'alert');

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await note.screenshot({ path: path.join(SHOT_DIR, `cli-repair-failed-${theme}.png`) });
    }
  });
});
