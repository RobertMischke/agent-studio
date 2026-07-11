import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Task Server settings section (AGT-1924).
 *
 * The new "Task Server" section of the consolidated Workspace-settings home is
 * the operator's read-context for the durable task server the platform talks
 * to: the connected URL (localhost today, a central URL in Phase 2), the
 * workspace store it owns, the git-backed evidence status, the registered
 * client identities, and the management sweeps (archive / orphan / fixture).
 *
 * The page renders from a static frontend snapshot (UI-first, no backend
 * dependency; only the connected URL is live from the serving origin), so this
 * spec stubs the shell's background polls and drives the rail. It asserts:
 *   - the rail exposes the "Task Server" section and the overview card;
 *   - the section renders the connection / store / evidence blocks, the client
 *     registry, and the management panel;
 *   - the summary client count reconciles to the visible client rows (R3);
 *   - running a sweep records a result row (optimistic);
 *   - a #/workspace/settings/task-server deep-link opens the section;
 *   - the section renders on the light theme too (R5).
 */

const SHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? 'test-results';

function settingsHome(page: Page) {
  return page.locator(
    '[data-testid="workspace-settings-inline"], [data-testid="workspace-settings-overlay"]',
  );
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/tasks', json([]));
  await page.route('**/api/tasks/grouped', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/workspaces*', json([]));
}

test.describe('Task Server settings section', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 950 });
    // Force the legacy (modal) layout so the section renders in the modal-backed
    // settings overlay (same choice as workspace-settings-home.spec).
    await page.addInitScript(() => { try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* ignore */ } });
    await stubBackgroundApis(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);
    await dismissDevErrorDialog(page);
  });

  test('rail + overview expose the Task Server section', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(settingsHome(page)).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-task-server')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-task-server')).toContainText('Task Server');
    await expect(page.getByTestId('workspace-settings-card-task-server')).toBeVisible();
  });

  test('section renders connection, store, evidence, clients, and management', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await dismissDevErrorDialog(page);
    await page.getByTestId('workspace-settings-rail-task-server').click();

    await expect(page.getByTestId('workspace-task-server-overlay')).toBeVisible();
    await expect(page.getByTestId('task-server-panel')).toBeVisible();

    await expect(page.getByTestId('task-server-connection')).toBeVisible();
    await expect(page.getByTestId('task-server-store')).toBeVisible();
    await expect(page.getByTestId('task-server-evidence')).toBeVisible();
    await expect(page.getByTestId('task-server-management')).toBeVisible();

    // The connected URL is the live serving origin.
    await expect(page.getByTestId('task-server-url')).toContainText(new URL(page.url()).origin);

    // Summary client count reconciles to the visible client rows (R3).
    const rows = page.locator('[data-testid="task-server-clients"] > li');
    const count = await rows.count();
    expect(count).toBeGreaterThanOrEqual(2);
    await expect(page.getByTestId('task-server-summary')).toContainText(String(count));

    await page.screenshot({ path: join(SHOT_DIR, 'task-server-section--mocked.png'), fullPage: false });
  });

  test('running a management sweep records a result row', async ({ page }) => {
    await page.goto('/#/workspace/settings/task-server');
    await expect(page.getByTestId('task-server-panel')).toBeVisible({ timeout: 5_000 });

    await expect(page.getByTestId('task-server-results-empty')).toBeVisible();
    await page.getByTestId('task-server-management-section').scrollIntoViewIfNeeded();
    await page.getByTestId('task-server-action-archive-sweep').click();
    await expect(page.getByTestId('task-server-result-archive-sweep')).toBeVisible({ timeout: 3_000 });
    // A second sweep so the results list shows more than one settled outcome.
    await page.getByTestId('task-server-action-orphan-scan').click();
    await expect(page.getByTestId('task-server-result-orphan-scan')).toBeVisible({ timeout: 3_000 });
    await page.getByTestId('task-server-management-section').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'task-server-management--mocked.png'), fullPage: false });
  });

  test('deep-link opens the Task Server section directly', async ({ page }) => {
    await page.goto('/#/workspace/settings/task-server');
    await page.waitForLoadState('domcontentloaded');
    await expect(settingsHome(page)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-rail-task-server')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('task-server-panel')).toBeVisible();
  });

  test('renders on the light theme too (R5)', async ({ page }) => {
    await page.goto('/#/workspace/settings/task-server');
    await page.waitForLoadState('domcontentloaded');
    await setTheme(page, 'light');
    await expect(page.getByTestId('task-server-panel')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('task-server-connection')).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'task-server-section-light--mocked.png'), fullPage: false });
  });
});
