import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Remote Hosts settings section (AGT-1921).
 *
 * The new "Remote hosts" section of the consolidated Workspace-settings home
 * lists every execution location - the operator's local machine and each remote
 * runner - in one list with heartbeat status, capabilities, system vitals
 * (RAM / CPU / Disk), per-CLI quota, and the Re-Probe / Drain / Retire actions.
 *
 * The page renders from a static frontend registry (UI-first, no backend
 * dependency), so this spec only stubs the shell's background polls and then
 * drives the rail. It asserts:
 *   - the rail exposes the "Remote hosts" section and the overview card;
 *   - the section renders one card per host with vitals + quota;
 *   - the header summary count reconciles to the visible cards (R3);
 *   - Drain and Retire mutate the target row's status;
 *   - a #/workspace/settings/remote-hosts deep-link opens the section.
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
  await page.route('**/api/watch-paths', json([{ name: 'agent-taskboard', path: 'C:/projects/agent-taskboard', rootPath: 'C:/projects' }]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/workspaces*', json([]));
}

test.describe('Remote Hosts settings section', () => {
  test.use({ serviceWorkers: 'block' });

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

  test('rail + overview expose the Remote hosts section', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(settingsHome(page)).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-remote-hosts')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-remote-hosts')).toContainText('Remote hosts');
    await expect(page.getByTestId('workspace-settings-card-remote-hosts')).toBeVisible();
  });

  test('section lists one card per host; summary reconciles to the cards (R3)', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await dismissDevErrorDialog(page);
    await page.getByTestId('workspace-settings-rail-remote-hosts').click();

    await expect(page.getByTestId('workspace-remote-hosts-overlay')).toBeVisible();
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();

    const cards = page.getByTestId('remote-host-card');
    const count = await cards.count();
    expect(count).toBeGreaterThanOrEqual(2);

    // Every card shows vitals + a status badge.
    await expect(page.getByTestId('remote-host-vitals').first()).toBeVisible();
    await expect(page.getByTestId('remote-host-status').first()).toBeVisible();

    // Header total equals the number of visible cards (R3 sum invariant).
    await expect(page.getByTestId('remote-hosts-summary')).toContainText(String(count));

    await page.screenshot({ path: join(SHOT_DIR, 'remote-hosts-section--mocked.png'), fullPage: false });
  });

  test('Drain and Retire mutate the target row status', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await dismissDevErrorDialog(page);
    await page.getByTestId('workspace-settings-rail-remote-hosts').click();
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();

    const firstCard = page.getByTestId('remote-host-card').first();
    await firstCard.getByTestId('remote-host-action-drain').click();
    await expect(firstCard.getByTestId('remote-host-status')).toContainText('Draining', { timeout: 3_000 });

    await firstCard.getByTestId('remote-host-action-retire').click();
    await expect(firstCard.getByTestId('remote-host-status')).toContainText('Retired', { timeout: 3_000 });
    await expect(firstCard.getByTestId('remote-host-no-stats')).toBeVisible();
  });

  test('configures one host and starts setup on the durable CLI task substrate', async ({ page }) => {
    let createBody: Record<string, unknown> | null = null;
    await page.unroute('**/api/tasks');
    await page.route('**/api/tasks', async route => {
      if (route.request().method() !== 'POST') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
        return;
      }
      createBody = route.request().postDataJSON();
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 'onboard-runner-02' }) });
    });
    await page.route('**/api/tasks/onboard-runner-02**', route => route.fulfill({
      status: 404,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'mocked-task-detail-not-mounted' }),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await remote.getByTestId('remote-host-action-setup').click();

    await expect(page.getByTestId('runner-setup-dialog')).toBeVisible();
    await expect(page.getByTestId('runner-setup-loopback-block')).toContainText('Loopback is not remotely reachable');
    await expect(page.getByTestId('visible-cli-task-card')).toBeHidden();

    await page.getByTestId('runner-setup-git-remote').fill('git@github.com:example/agent-studio.git');
    await page.getByTestId('runner-setup-connection-mode').selectOption('tunnel');

    await expect(page.getByTestId('visible-cli-task-card')).toBeVisible();
    await expect(page.getByTestId('visible-cli-task-prompt')).toContainText('Reachability gate (must run first)');
    await expect(page.getByTestId('visible-cli-task-prompt')).toContainText('codex login --device-auth');
    await expect(page.getByTestId('visible-cli-task-duration')).toContainText('10 to 20 minutes plus operator login time');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-runner-setup--mocked.png'), fullPage: false });
    await page.getByTestId('visible-cli-task-start').click();

    await expect.poll(() => createBody).not.toBeNull();
    expect(createBody).toMatchObject({
      title: 'Set up runner on agent-runner-01',
      agent: 'codex',
      targetState: '2-ready',
      watchPath: 'C:/projects/agent-taskboard',
    });
    expect(String(createBody?.['promptMarkdown'])).toContain('## CLI input');
    expect(String(createBody?.['promptMarkdown'])).toContain('bash scripts/remote-runner-onboard.sh');
    expect(String(createBody?.['promptMarkdown'])).toContain("--host 'agent-runner'");
    expect(String(createBody?.['promptMarkdown'])).toContain('X-Client-Id: agent-runner-01');
    expect(String(createBody?.['promptMarkdown'])).toContain('Never copy, upload, or reuse credential files');
  });

  test('adds a host through the guided four-step setup', async ({ page }) => {
    await page.goto('/#/workspace/settings/remote-hosts');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible({ timeout: 5_000 });
    await page.getByTestId('remote-hosts-add').click();
    await expect(page.getByTestId('add-host-wizard')).toBeVisible();

    await page.getByTestId('add-host-connect-check').check();
    await page.getByTestId('add-host-next').click();
    await page.getByTestId('add-host-provision-check').check();
    await page.getByTestId('add-host-next').click();
    await page.getByTestId('add-host-claude-check').check();
    await page.getByTestId('add-host-codex-check').check();
    await page.getByTestId('add-host-next').click();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-add-wizard--mocked.png'), fullPage: false });
    await page.getByTestId('add-host-smoke-check').check();
    await page.getByTestId('add-host-next').click();

    await expect(page.getByTestId('add-host-wizard')).toBeHidden();
    await expect(page.getByTestId('remote-host-name').filter({ hasText: 'agent-runner-02' })).toBeVisible();
  });

  test('renders on the light theme too (R5)', async ({ page }) => {
    await page.goto('/#/workspace/settings/remote-hosts');
    await page.waitForLoadState('domcontentloaded');
    await setTheme(page, 'light');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('remote-host-card').first()).toBeVisible();
    await expect(page.getByTestId('remote-host-vitals').first()).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-hosts-section-light--mocked.png'), fullPage: false });

    await page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' })
      .getByTestId('remote-host-action-setup').click();
    await expect(page.getByTestId('runner-setup-dialog')).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-runner-setup-light--mocked.png'), fullPage: false });
  });

  test('deep-link opens the Remote hosts section directly', async ({ page }) => {
    await page.goto('/#/workspace/settings/remote-hosts');
    await page.waitForLoadState('domcontentloaded');
    await expect(settingsHome(page)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-rail-remote-hosts')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();
  });
});
