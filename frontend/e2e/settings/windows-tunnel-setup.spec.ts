import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Windows control-plane tunnel keeper + watchdog panel (AGT-2664).
 *
 * The "Windows tunnel keeper" panel lives in the local host's Execution Hosts
 * card (Connection section) and, as a local prerequisite, inside the
 * "Set up agent host" dialog when Reverse tunnel mode is chosen. It polls
 * GET /api/v1/management/windows-tunnel/status and drives registration
 * through POST /api/v1/management/windows-tunnel/register.
 */

const SHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? '../results/windows-tunnel-setup';

function windowsTunnelStatus(overrides: Record<string, unknown> = {}) {
  const now = new Date().toISOString();
  return {
    platform: 'windows',
    observedAt: now,
    keeperTask: {
      taskName: 'AgentRunner-TunnelKeeper', registered: true, state: 'Ready',
      lastRunTime: now, lastTaskResult: 0, nextRunTime: now,
    },
    keeperHealth: { status: 'healthy', message: 'Replacement forward passed the remote functional probe.', observedAt: now, repairAttempts: 0 },
    watchdogTask: {
      taskName: 'AgentRunner-TunnelWatchdog', registered: true, state: 'Running',
      lastRunTime: now, lastTaskResult: null, nextRunTime: null,
    },
    watchdogHealth: {
      lastHealSucceededAt: now, lastHealFailedAt: null,
      lastProbeFailedAt: null, lastEvent: 'heal_succeeded', lastEventAt: now,
    },
    alarmActive: false,
    detail: null,
    ...overrides,
  };
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/tasks', json([]));
  await page.route('**/api/tasks/grouped', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/auth/status', json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/crash-recovery/pending', json({ pending: [] }));
  await page.route('**/api/watch-paths', json([{ name: 'agent-taskboard', path: 'C:/projects/agent-taskboard', rootPath: 'C:/projects' }]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/queue-starvation', json({
    active: false, waitingTaskCount: 0, availableSlots: 0, thresholdMinutes: 30,
    observedAt: new Date().toISOString(), oldestEnteredLaneAt: null, items: [],
  }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  const now = new Date().toISOString();
  await page.route('**/api/clients', json([
    { id: 'local-default', displayName: 'operator-workstation', kind: 'human', registeredAt: now, lastSeenAt: now },
    { id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service', registeredAt: now, lastSeenAt: now,
      runnerGitStatus: 'ready', runnerGitCheckedAt: now, runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2,
      runnerActiveGateCount: 0, runnerGateCapacity: 4 },
  ]));
  await page.route('**/api/v1/management/remote-hosts', json([]));
  await page.route('**/api/v1/management/windows-tunnel/status', json(windowsTunnelStatus()));
  await page.route('**/api/clients/*/telemetry?window=*', json({ clientId: 'mock', window: '14d', points: [{
    timestamp: now, cpuPercent: 7, load1: 0.1, load5: 0.1, load15: 0.1,
    memoryUsedBytes: 4_000_000_000, memoryTotalBytes: 16_000_000_000,
    swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0, cpuStealPercent: 0, ioWaitPercent: 0,
    cpuCores: 4, activeSlots: 0,
  }], findings: [] }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/workspaces*', json([]));
}

test.describe('Windows tunnel keeper panel', () => {
  test.use({ serviceWorkers: 'block' });

  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 950 });
    await page.addInitScript(() => { try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* ignore */ } });
    await stubBackgroundApis(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);
    await dismissDevErrorDialog(page);
  });

  test('shows registered/healthy status on the local host card in both themes', async ({ page }) => {
    await page.goto('/#/workspace/settings/execution-hosts');
    const local = page.getByTestId('remote-host-card').filter({ hasText: 'Local machine' });
    await local.getByTestId('remote-host-disclosure').click();
    await expect(local.getByTestId('remote-host-detail-row')).toBeVisible();

    const panel = local.getByTestId('windows-tunnel-setup');
    await expect(panel).toBeVisible();
    await expect(panel).toHaveAttribute('data-state', 'ok');
    await expect(panel).toContainText('Registered and healthy');
    await expect(local.getByTestId('windows-tunnel-keeper-state')).toContainText('Ready');
    await expect(local.getByTestId('windows-tunnel-watchdog-state')).toContainText('Running');

    await setTheme(page, 'light');
    await panel.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'windows-tunnel-healthy-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'windows-tunnel-healthy-dark--mocked.png'), fullPage: false });
  });

  test('shows the not-registered state with the elevation-consent copy and a working register button', async ({ page }) => {
    await page.unroute('**/api/v1/management/windows-tunnel/status');
    await page.route('**/api/v1/management/windows-tunnel/status', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify(windowsTunnelStatus({
        keeperTask: { taskName: 'AgentRunner-TunnelKeeper', registered: false, state: null, lastRunTime: null, lastTaskResult: null, nextRunTime: null },
        keeperHealth: { status: null, message: null, observedAt: null, repairAttempts: null },
        watchdogTask: { taskName: 'AgentRunner-TunnelWatchdog', registered: false, state: null, lastRunTime: null, lastTaskResult: null, nextRunTime: null },
        watchdogHealth: { lastHealSucceededAt: null, lastHealFailedAt: null, lastProbeFailedAt: null, lastEvent: null, lastEventAt: null },
      })),
    }));
    let registerCalled = false;
    await page.route('**/api/v1/management/windows-tunnel/register', route => {
      registerCalled = true;
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          platform: 'windows', ok: true, elevated: true,
          detail: 'Scheduled tasks registered: keeper registered, watchdog registered.',
          requestedAt: new Date().toISOString(),
        }),
      });
    });

    await page.goto('/#/workspace/settings/execution-hosts');
    const local = page.getByTestId('remote-host-card').filter({ hasText: 'Local machine' });
    await local.getByTestId('remote-host-disclosure').click();
    const panel = local.getByTestId('windows-tunnel-setup');
    await expect(panel).toHaveAttribute('data-state', 'not-registered');
    await expect(panel).toContainText('administrator rights once');
    await expect(panel).toContainText('User Account Control');

    await setTheme(page, 'light');
    await panel.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'windows-tunnel-not-registered-light--mocked.png'), fullPage: false });

    await local.getByTestId('windows-tunnel-register').click();
    await expect(local.getByTestId('windows-tunnel-register-result')).toContainText('Scheduled tasks registered');
    expect(registerCalled).toBe(true);
    await page.screenshot({ path: join(SHOT_DIR, 'windows-tunnel-register-result-light--mocked.png'), fullPage: false });
  });

  test('the "Set up agent host" dialog shows the local prerequisite in Reverse tunnel mode', async ({ page }) => {
    await page.goto('/#/workspace/settings/execution-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await remote.getByTestId('remote-host-disclosure').click();
    await remote.getByTestId('remote-host-action-setup').click();

    const dialog = page.getByTestId('runner-setup-dialog');
    await expect(dialog).toBeVisible();
    await dialog.getByTestId('runner-setup-connection-mode').selectOption('tunnel');

    const prerequisite = dialog.getByTestId('runner-setup-tunnel-prerequisite');
    await expect(prerequisite).toBeVisible();
    await expect(prerequisite).toContainText('Windows control-plane host');
    await expect(prerequisite.getByTestId('windows-tunnel-setup')).toBeVisible();

    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'runner-setup-tunnel-prerequisite-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'runner-setup-tunnel-prerequisite-dark--mocked.png'), fullPage: false });
  });
});
