import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import type { Page, Route } from '@playwright/test';
import { expect, test } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env['JOB_RESULTS_DIR'] ?? '../results/status-bar-cli-repair';
mkdirSync(RESULTS_DIR, { recursive: true });

function json(body: unknown) {
  return (route: Route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function stubShell(page: Page): Promise<void> {
  await page.route('**/api/**', json([]));
  await page.route('**/api/auth/status', json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', json({ isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/tasks/grouped', json({
    preparation: [], ready: [], progress: [], review: [], completed: [], archive: [],
  }));
  await page.route(/\/api\/runner\/status(?:\?.*)?$/, json({
    projects: {},
    cliRepairs: [{
      cliType: 'claude',
      repairedAt: '2026-08-18T09:14:00Z',
      cliVersionBefore: '2.1.231',
      cliVersionAfter: '2.1.234',
      packageVersionBefore: '2.1.231',
      packageVersionAfter: '2.1.234',
    }],
  }));
  await page.route('**/api/cli/quota**', json({ at: '2026-08-18T09:15:00Z', snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/v1/management/remote-hosts', json([]));
}

test.describe('Local CLI repair status note', () => {
  test.use({ serviceWorkers: 'block' });

  test('shows a quiet successful repair note in both themes', async ({ page, devBackend }) => {
    void devBackend;
    await stubShell(page);
    await page.goto('/');

    const note = page.getByTestId('status-bar-cli-repaired');
    await expect(note).toContainText('CLI repaired at');
    await note.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('Version 2.1.231 to 2.1.234');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('status-bar').screenshot({
        path: join(RESULTS_DIR, `status-bar-cli-repaired-${theme}--mocked.png`),
      });
    }
  });
});
