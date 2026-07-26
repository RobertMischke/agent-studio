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
 * Client lifecycle and activity come from the persisted Task Server API.
 *   - the rail exposes the "Remote hosts" section and the overview card;
 *   - the section renders one card per host with vitals + quota;
 *   - the header summary count reconciles to the visible cards (R3);
 *   - Drain and graceful Retire call the API, confirm impact, and preserve retired clients;
 *   - a #/workspace/settings/remote-hosts deep-link opens the section.
 */

const SHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? '../results/remote-hosts';

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
  await page.route('**/api/auth/status', json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/watch-paths', json([{ name: 'agent-taskboard', path: 'C:/projects/agent-taskboard', rootPath: 'C:/projects' }]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  const now = new Date().toISOString();
  await page.route('**/api/clients', json([
    { id: 'local-default', displayName: 'operator-workstation', kind: 'human', registeredAt: now, lastSeenAt: now },
    { id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service', registeredAt: now, lastSeenAt: now,
      runnerGitStatus: 'ready', runnerGitCheckedAt: now, runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2,
      runnerActiveGateCount: 0, runnerGateCapacity: 4 },
  ]));
  await page.route('**/api/v1/management/remote-hosts', json([]));
  await page.route('**/api/clients/*/telemetry?window=14d', json({ clientId: 'mock', window: '14d', points: [{
    timestamp: now, cpuPercent: 7, load1: 0.1, load5: 0.1, load15: 0.1,
    memoryUsedBytes: 4_000_000_000, memoryTotalBytes: 16_000_000_000,
    swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0, cpuStealPercent: 0, ioWaitPercent: 0,
    cpuCores: 4, activeSlots: 0,
  }], findings: [] }));
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

  test('first mount waits for live status and then paints the daemon without reload', async ({ page }) => {
    let releaseResponse!: () => void;
    const responseGate = new Promise<void>(resolve => { releaseResponse = resolve; });
    const now = new Date().toISOString();
    await page.unroute('**/api/clients');
    await page.unroute('**/api/clients/*/telemetry?window=14d');
    await page.route('**/api/clients', async route => {
      await responseGate;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: now, lastSeenAt: now, runnerGitStatus: 'ready',
        runnerDaemonState: 'running', runnerActiveSlots: 1, runnerAvailableSlots: 19,
        runnerActiveGateCount: 2, runnerGateCapacity: 4,
      }]) });
    });
    await page.route('**/api/clients/agent-runner-01/telemetry?window=14d', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({
        clientId: 'agent-runner-01', window: '14d', findings: [], points: [{
          timestamp: now, cpuPercent: 53, load1: 7.7, load5: 7.1, load15: 6.8,
          memoryUsedBytes: 34_000_000_000, memoryTotalBytes: 64_000_000_000,
          swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0, cpuStealPercent: 0,
          ioWaitPercent: 2.1, cpuCores: 12, activeSlots: 1,
        }],
      }),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(remote.getByTestId('remote-host-status')).toContainText('Loading live status');
    await expect(remote).not.toContainText('Daemonstopped');

    releaseResponse();

    await expect(remote.getByTestId('remote-host-status')).toContainText('Online');
    await expect(remote.getByTestId('remote-host-activity')).toContainText('Daemonrunning');
    await expect(remote.getByTestId('remote-host-run-pool')).toContainText('1 active · 19 free · 20 max');
    await expect(remote.getByTestId('remote-host-gate-pool')).toContainText('2 running · pool 4');
    await expect(remote.getByTestId('remote-host-cpu-context')).toContainText('GATE work does not consume a RUN slot');
    await expect(remote.getByTestId('remote-host-vitals')).toContainText('53%');
    await expect(remote.getByTestId('remote-host-slots-context')).toContainText('1 RUN active · host load 7.7 of 12 cores');
    await remote.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-live-first-mount--mocked.png'), fullPage: false });
  });

  test('Drain and graceful Retire require confirmation and keep a revivable retired client', async ({ page }) => {
    let kind = 'service';
    let draining = false;
    const now = new Date().toISOString();
    await page.unroute('**/api/clients');
    await page.route('**/api/clients/*/drain', async route => { draining = true; await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); });
    await page.route('**/api/clients/*/retire', async route => { draining = true; kind = 'retired'; await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); });
    await page.route('**/api/clients/*/revive', async route => { draining = false; kind = 'service'; await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); });
    await page.route('**/api/clients', async route => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind, registeredAt: now, lastSeenAt: now,
        drainRequestedAt: draining ? now : null, retireRequestedAt: draining ? now : null,
        runnerGitStatus: 'ready', runnerDaemonState: kind === 'retired' ? 'stopped' : 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2,
      }]) });
    });
    await page.goto('/#/workspace/settings/remote-hosts');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();

    let card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(card.getByTestId('remote-host-action-drain')).toHaveAttribute('title', /No new leases|Stop new leases/);
    await card.getByTestId('remote-host-action-drain').click();
    await expect(card.getByTestId('remote-host-status')).toContainText('Draining');

    await card.getByTestId('remote-host-action-retire').click();
    await expect(page.getByTestId('remote-host-confirm')).toContainText('No new leases');
    await expect(page.getByTestId('remote-host-confirm')).toContainText('remains visible and can be revived');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-retire-confirm-dark.png'), fullPage: false });
    await page.getByTestId('remote-host-confirm-submit').click();
    await expect(page.getByTestId('remote-hosts-retired')).toBeVisible();
    await page.getByTestId('remote-hosts-retired').locator('summary').click();
    card = page.getByTestId('remote-hosts-retired').getByTestId('remote-host-card');
    await expect(card.getByTestId('remote-host-status')).toContainText('Retired');
    await card.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-retired-revive-dark.png'), fullPage: false });
    await card.getByTestId('remote-host-action-revive').click();
    await expect(page.getByTestId('remote-hosts-active').getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' })).toBeVisible();
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

    await page.getByTestId('runner-setup-git-remote').fill('https://github.com/example/agent-studio.git');
    await page.getByTestId('runner-setup-git-push-remote').fill('git@github.com:example/agent-studio.git');
    await page.getByTestId('runner-setup-connection-mode').selectOption('tunnel');

    await expect(page.getByTestId('visible-cli-task-card')).toBeVisible();
    await expect(page.getByTestId('visible-cli-task-prompt')).toContainText('Reachability gate (must run first)');
    await expect(page.getByTestId('visible-cli-task-prompt')).toContainText('codex login --device-auth');
    await expect(page.getByTestId('visible-cli-task-duration')).toContainText('10 to 20 minutes plus operator login time');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-runner-setup--mocked.png'), fullPage: false });
    await page.getByTestId('visible-cli-task-start').click();

    await expect.poll(() => createBody).not.toBeNull();
    expect(createBody).toMatchObject({
      title: 'Set up agent host on agent-runner-01',
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

  test('surfaces a failed startup push probe as a read-only host', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: '2026-07-11T19:00:00Z', lastSeenAt: new Date().toISOString(),
        runnerGitStatus: 'read-only', runnerGitDetail: 'push-dry-run failed (128): permission denied',
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    const badge = remote.getByTestId('remote-host-git-status');
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('Writable: no');
    await badge.hover();
    await expect(page.getByRole('tooltip')).toContainText('permission denied');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-read-only--mocked.png'), fullPage: false });
  });

  test('shows selective capability drain, canary context, affected claims, and recovery history without freshening stale metrics', async ({ page }) => {
    const now = Date.now();
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        runnerId: 'agent-runner-01',
        name: 'agent-runner-01',
        hostId: 'host-berlin',
        instanceId: 'coding-codex',
        runnerVersion: '1.2.0',
        protocolVersion: 2,
        status: 'active',
        registeredAt: new Date(now - 86_400_000).toISOString(),
        lastSeenAt: new Date(now - 15_000).toISOString(),
        hostAdmission: {
          hostId: 'host-berlin',
          admissionState: 'open',
          automaticDrainReason: null,
          automaticDrainAt: null,
          operatorDrainReason: null,
          operatorDrainAt: null,
        },
        capabilities: [{
          key: 'provider-auth:codex',
          category: 'provider-auth',
          advertisedStatus: 'ready',
          healthState: 'draining',
          reason: 'ProviderUnauthorized: Codex returned 401',
          advertisedAt: new Date(now - 30_000).toISOString(),
          freshUntil: new Date(now + 120_000).toISOString(),
          isFresh: true,
          firstFailureAt: new Date(now - 90_000).toISOString(),
          lastFailureAt: new Date(now - 30_000).toISOString(),
          cooldownUntil: new Date(now + 90_000).toISOString(),
          canaryClaimId: 'run_canary',
          consecutiveFailures: 2,
          version: 'available',
          identity: 'codex',
          affectedClaims: ['run:run_active', 'review:review_active'],
          recoveryHistory: [{
            occurredAt: new Date(now - 30_000).toISOString(),
            fromState: 'suspect',
            toState: 'draining',
            reason: 'Codex returned 401',
            claimId: 'run_active',
          }],
        }],
        telemetry: {
          observedAt: new Date(now - 10 * 60_000).toISOString(),
          cpuPercent: 99,
          memoryUsedBytes: 15_000_000_000,
          memoryTotalBytes: 16_000_000_000,
          cpuCores: 4,
          diskFreeBytes: 1,
          diskTotalBytes: 100,
        },
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    const capability = card.getByTestId('remote-host-capability-provider-auth:codex');
    await expect(capability).toContainText('draining');
    await expect(capability).toContainText('Codex returned 401');
    await expect(capability).toContainText('run_canary');
    await expect(capability).toContainText('run:run_active, review:review_active');
    await capability.getByText('Recovery history · 1').click();
    await expect(capability).toContainText('suspect → draining');
    await expect(card.getByTestId('remote-host-vitals')).not.toContainText('99%');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-capability-drain--mocked.png'), fullPage: false });
  });

  test('labels automatic whole-host drain separately from operator-requested drain', async ({ page }) => {
    const now = new Date().toISOString();
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'host-berlin',
        instanceId: 'coding', runnerVersion: '1.2.0', protocolVersion: 2,
        status: 'active', registeredAt: now, lastSeenAt: now,
        hostAdmission: {
          hostId: 'host-berlin', admissionState: 'automatic-draining',
          automaticDrainReason: 'host:disk: DiskFull', automaticDrainAt: now,
          operatorDrainReason: 'planned maintenance', operatorDrainAt: now,
        },
        capabilities: [],
        telemetry: null,
      }]),
    }));
    await page.goto('/#/workspace/settings/remote-hosts');
    const admission = page.getByTestId('remote-host-card')
      .filter({ hasText: 'agent-runner-01' })
      .getByTestId('remote-host-admission');
    await expect(admission).toContainText('Automatic whole-host drain');
    await expect(admission).toContainText('host:disk');
    await expect(admission).not.toContainText('Operator-requested');
    const operatorAdmission = page.getByTestId('remote-host-card')
      .filter({ hasText: 'agent-runner-01' })
      .getByTestId('remote-host-operator-admission');
    await expect(operatorAdmission).toContainText('Operator-requested host drain');
    await expect(operatorAdmission).toContainText('planned maintenance');
    await expect(operatorAdmission).not.toContainText('Automatic');
  });

  test('shows a failed project delivery preflight and its claim refusal reason', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: '2026-07-22T10:00:00Z', lastSeenAt: new Date().toISOString(),
        runnerGitStatus: 'ready',
        runnerProjectPreflights: [{
          projectId: 'PROJ-042', projectName: 'Payments', registrationFingerprint: 'a'.repeat(64),
          repositoryUrl: 'https://github.com/example/payments.git',
          fetchUrl: 'https://github.com/example/payments.git',
          pushUrl: 'https://github.com/example/payments.git', status: 'failed',
          detail: 'write probe failed (128): permission denied', checkedAt: '2026-07-22T10:01:00Z',
        }],
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    const failure = remote.getByTestId('remote-host-project-preflight-failures');
    await expect(failure).toContainText('Payments');
    await expect(failure).toContainText('permission denied');
    await remote.screenshot({ path: join(SHOT_DIR, 'remote-host-project-preflight-failed--mocked.png') });
  });

  test('shows persisted performance history, slot context, and a throttling finding', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: '2026-07-11T18:00:00Z', lastSeenAt: new Date().toISOString(),
      }]),
    }));
    const now = Date.now();
    const points = Array.from({ length: 8 }, (_, index) => ({
      timestamp: new Date(now - (7 - index) * 30_000).toISOString(),
      cpuPercent: 44 + index * 3, load1: 5.7 + index * 0.1, load5: 5.4, load15: 5.1,
      memoryUsedBytes: 36_000_000_000 + index * 300_000_000, memoryTotalBytes: 64_000_000_000,
      swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0, cpuStealPercent: 6.2, ioWaitPercent: 2.1,
      cpuCores: 12, activeSlots: index < 4 ? 5 : 6,
    }));
    await page.route('**/api/clients/agent-runner-01/telemetry?window=14d', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({
        clientId: 'agent-runner-01', window: '14d', points,
        findings: [{ kind: 'vm-throttled', label: 'VM throttled', since: points[0].timestamp, until: points.at(-1)?.timestamp }],
      }),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(remote.getByTestId('remote-host-telemetry')).toBeVisible();
    await expect(remote.getByTestId('remote-host-slots-context')).toContainText('6 RUN active · host load 6.4 of 12 cores');
    await expect(remote.getByTestId('remote-host-findings')).toContainText('VM throttled');
    await expect(remote.locator('[data-chart]')).toHaveCount(4);

    const inspectedIndex = 4;
    const plots = remote.getByTestId('remote-host-telemetry-plots');
    await plots.scrollIntoViewIfNeeded();
    const bounds = await plots.boundingBox();
    expect(bounds).not.toBeNull();
    await plots.hover({ position: {
      x: bounds!.width * inspectedIndex / (points.length - 1),
      y: bounds!.height / 2,
    } });
    const tooltip = remote.getByTestId('remote-host-telemetry-tooltip');
    await expect(tooltip).toBeVisible();
    await expect(tooltip).toHaveAttribute('data-point-timestamp', points[inspectedIndex].timestamp);
    await expect(tooltip.locator('[data-metric="cpu"]')).toHaveText('56%');
    await expect(tooltip.locator('[data-metric="memory"]')).toHaveText('37.2 GB');
    await expect(tooltip.locator('[data-metric="load"]')).toHaveText('6.1 load');
    await expect(tooltip.locator('[data-metric="slots"]')).toHaveText('6 slots');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-telemetry-tooltip--mocked.png'), fullPage: false });
    await setTheme(page, 'light');
    await expect(tooltip).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-telemetry-tooltip-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');

    await remote.getByTestId('remote-host-window-1h').click();
    await expect(remote.getByTestId('remote-host-window-1h')).toHaveAttribute('aria-pressed', 'true');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-telemetry--mocked.png'), fullPage: false });
  });

  test('adds a host through the guided five-step setup including deploy key', async ({ page }) => {
    await page.goto('/#/workspace/settings/remote-hosts');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible({ timeout: 5_000 });
    await page.getByTestId('remote-hosts-add').click();
    await expect(page.getByTestId('add-host-wizard')).toBeVisible();

    await page.getByTestId('add-host-connect-check').check();
    await page.getByTestId('add-host-next').click();
    await page.getByTestId('add-host-provision-check').check();
    await page.getByTestId('add-host-next').click();
    await expect(page.getByTestId('add-host-wizard')).toContainText('write-enabled repository deploy key');
    await page.getByTestId('add-host-deploy-key-check').check();
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
    await page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-hosts-section-light--mocked.png'), fullPage: false });

    await page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' })
      .getByTestId('remote-host-action-setup').click();
    await expect(page.getByTestId('runner-setup-dialog')).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-runner-setup-light--mocked.png'), fullPage: false });
  });

  test('never renders stale CPU as live and captures dark-theme evidence', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service', registeredAt: '2026-07-09T09:00:00Z',
      lastSeenAt: '2026-07-09T09:00:00Z', runnerGitStatus: 'ready', runnerGitCheckedAt: '2026-07-09T09:00:00Z',
      runnerDaemonState: 'running', runnerActiveSlots: 2, runnerAvailableSlots: 0,
    }]) }));
    await page.goto('/#/workspace/settings/remote-hosts');
    await setTheme(page, 'dark');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(remote.getByTestId('remote-host-stale')).toContainText('Historical metrics are hidden');
    await expect(remote.getByTestId('remote-host-vitals')).toHaveCount(0);
    await expect(remote).not.toContainText('54%');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-hosts-stale-dark.png'), fullPage: false });
  });

  test('deep-link opens the Remote hosts section directly', async ({ page }) => {
    await page.goto('/#/workspace/settings/remote-hosts');
    await page.waitForLoadState('domcontentloaded');
    await expect(settingsHome(page)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-rail-remote-hosts')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();
  });
});
