import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-2673. The backend repairs a locally broken CLI by itself; the status bar
 * is what keeps that fix from being silent. A successful repair is a quiet
 * note, a failed one is the only acute signal.
 */
const RESULTS_DIR = process.env['JOB_RESULTS_DIR'] ?? '../results/status-bar';
mkdirSync(RESULTS_DIR, { recursive: true });

function json(body: unknown) {
  return (route: Route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function healthy(cliType: string) {
  return {
    cliType,
    packageId: `@example/${cliType}`,
    state: 'Ready',
    action: 'None',
    summary: `${cliType} answers --version (2.1.234).`,
    available: true,
    version: '2.1.234',
    packageVersion: '2.1.234',
  };
}

function broken(cliType: string) {
  return {
    ...healthy(cliType),
    state: 'ShimMissingPackagePresent',
    action: 'GlobalReinstall',
    summary: `${cliType} bin shims are missing while the package is still installed.`,
    available: false,
    version: null,
  };
}

async function stubHostHealth(page: Page, snapshot: unknown): Promise<void> {
  await page.route('**/api/auth/status', json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
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
  await page.route('**/api/v1/host-health/cli', json(snapshot));
}

/** Minutes ago, so the rendered `HH:MM` is stable relative to the run. */
function minutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000).toISOString();
}

function expectedTime(minutes: number): string {
  const at = new Date(Date.now() - minutes * 60_000);
  return `${`${at.getHours()}`.padStart(2, '0')}:${`${at.getMinutes()}`.padStart(2, '0')}`;
}

test.describe('Status bar local CLI repair note', () => {
  test.use({ serviceWorkers: 'block' });

  test('a healthy host says nothing at all', async ({ page }) => {
    await stubHostHealth(page, {
      checkedAt: new Date().toISOString(),
      clis: [healthy('claude'), healthy('codex')],
      recentRepairs: [],
    });
    await page.goto('/');

    await expect(page.getByTestId('status-bar')).toBeVisible();
    await expect(page.getByTestId('status-bar-cli-repair')).toHaveCount(0);
  });

  test('a successful repair leaves a quiet note with the version change', async ({ page }) => {
    await stubHostHealth(page, {
      checkedAt: new Date().toISOString(),
      clis: [healthy('claude'), healthy('codex')],
      recentRepairs: [{
        cliType: 'claude',
        at: minutesAgo(12),
        repaired: true,
        state: 'Ready',
        message: 'claude CLI repaired (bin shims were missing).',
        versionBefore: '2.1.231',
        versionAfter: '2.1.234',
      }],
    });
    await page.goto('/');

    const note = page.getByTestId('status-bar-cli-repair');
    await expect(note).toContainText(`claude CLI repaired at ${expectedTime(12)}`);
    await note.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('Version 2.1.231 -> 2.1.234.');

    await setTheme(page, 'dark');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repaired-dark--mocked.png'),
    });
    await setTheme(page, 'light');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repaired-light--mocked.png'),
    });
  });

  test('a failed repair is the only acute signal, and only while the CLI is broken', async ({ page }) => {
    await stubHostHealth(page, {
      checkedAt: new Date().toISOString(),
      clis: [broken('claude'), healthy('codex')],
      recentRepairs: [{
        cliType: 'claude',
        at: minutesAgo(3),
        repaired: false,
        state: 'ShimMissingPackagePresent',
        message: 'claude CLI repair failed: npm exited 1',
        versionBefore: '2.1.231',
        versionAfter: '2.1.231',
      }],
    });
    await page.goto('/');

    const note = page.getByTestId('status-bar-cli-repair');
    await expect(note).toContainText('claude CLI repair failed');
    await expect(page.getByTestId('status-bar-cli-repair-divergence'))
      .toHaveAttribute('aria-label', 'Local CLI repair failed');

    await setTheme(page, 'dark');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repair-failed-dark--mocked.png'),
    });
    await setTheme(page, 'light');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repair-failed-light--mocked.png'),
    });
  });

  test('an old failure drops off once the CLI is healthy again', async ({ page }) => {
    await stubHostHealth(page, {
      checkedAt: new Date().toISOString(),
      clis: [healthy('claude'), healthy('codex')],
      recentRepairs: [{
        cliType: 'claude',
        at: minutesAgo(90),
        repaired: false,
        state: 'ShimMissingPackagePresent',
        message: 'claude CLI repair failed: npm exited 1',
      }],
    });
    await page.goto('/');

    await expect(page.getByTestId('status-bar')).toBeVisible();
    await expect(page.getByTestId('status-bar-cli-repair')).toHaveCount(0);
  });
});
