import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env['JOB_RESULTS_DIR'] ?? '../results/status-bar';
mkdirSync(RESULTS_DIR, { recursive: true });

function json(body: unknown) {
  return (route: Route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function stubHostLoad(
  page: Page,
  localRuns: number,
  remoteRuns: number,
  telemetrySlots: number,
  load1: number,
): Promise<void> {
  const now = new Date().toISOString();
  const baseTask = (id: string, projectName: string) => ({
    id,
    taskKey: id,
    title: id,
    state: '3-progress',
    order: 0,
    agent: '',
    createdAt: now,
    watchPath: '/mock',
    projectName,
    folderPath: '',
    lastActivity: now,
    sessionName: null,
    model: 'test',
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
  });
  const local = Array.from({ length: localRuns }, (_, index) => ({
    ...baseTask(`local-${index + 1}`, `local-project-${index + 1}`),
    execution: { status: 'running', startedAt: now },
  }));
  const remote = Array.from({ length: remoteRuns }, (_, index) => ({
    ...baseTask(`remote-${index + 1}`, `remote-project-${index + 1}`),
    runner: {
      runnerId: 'agent-runner-01',
      runnerName: 'agent-runner-01',
      hostname: 'agent-runner-01',
      backendName: 'task-server',
      isRemote: true,
      leaseId: `lease-${index + 1}`,
      fencingToken: index + 1,
      acquiredAt: now,
    },
  }));

  await page.route('**/api/auth/status', json({
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));
  await page.route('**/api/environment**', json({ isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/tasks/grouped', json({
    preparation: [],
    ready: [],
    progress: [...local, ...remote],
    review: [],
    completed: [],
    archive: [],
  }));
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/clients', json([{
    id: 'agent-runner-01',
    displayName: 'agent-runner-01',
    kind: 'service',
    registeredAt: now,
    lastSeenAt: now,
    runnerGitStatus: 'ready',
    runnerDaemonState: 'running',
    runnerActiveSlots: telemetrySlots,
    runnerAvailableSlots: Math.max(0, 8 - telemetrySlots),
  }]));
  await page.route('**/api/clients/agent-runner-01/telemetry?window=1h', json({
    clientId: 'agent-runner-01',
    window: '1h',
    points: [{
      timestamp: now,
      cpuPercent: 68,
      load1,
      load5: load1,
      load15: load1,
      memoryUsedBytes: 24_000_000_000,
      memoryTotalBytes: 64_000_000_000,
      swapInBytesPerSecond: 0,
      swapOutBytesPerSecond: 0,
      cpuStealPercent: 0,
      ioWaitPercent: 0,
      cpuCores: 12,
      activeSlots: telemetrySlots,
    }],
    findings: [],
  }));
  await page.route('**/api/clients/agent-runner-01/telemetry?window=14d', json({
    clientId: 'agent-runner-01',
    window: '14d',
    points: [{
      timestamp: now,
      cpuPercent: 68,
      load1,
      load5: load1,
      load15: load1,
      memoryUsedBytes: 24_000_000_000,
      memoryTotalBytes: 64_000_000_000,
      swapInBytesPerSecond: 0,
      swapOutBytesPerSecond: 0,
      cpuStealPercent: 0,
      ioWaitPercent: 0,
      cpuCores: 12,
      activeSlots: runningCount,
    }],
    findings: [],
  }));
  await page.route('**/api/v1/management/remote-hosts', json([]));
}

test.describe('Status bar execution-host load companion signal', () => {
  test.use({ serviceWorkers: 'block' });

  test('corresponding run count and load share the existing pulse point', async ({ page }) => {
    await stubHostLoad(page, 1, 3, 3, 7.2);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('4 running');
    await expect(running).toHaveAttribute('data-signal-tone', 'working');
    await expect(running).toHaveAttribute('data-signal-correlation', 'consistent');
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('Open execution hosts');
    await expect(page.getByTestId('cac-tooltip')).toContainText('Execution host load 7.2 / 12 cores (60%)');
    await expect(page.getByTestId('cac-tooltip')).toContainText('4 active execution slots');
  });

  test('click opens Execution Hosts management', async ({ page }) => {
    await stubHostLoad(page, 2, 3.6);
    await page.goto('/');

    await page.getByTestId('status-bar-running').click();

    await expect(page).toHaveURL(/#\/workspace\/settings\/execution-hosts(?:&|$)/);
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();
    await expect(page.getByTestId('remote-hosts-panel').getByRole('heading', { level: 2 }))
      .toHaveText('Execution Hosts');
  });

  test('high load without runs becomes a quiet hint in both themes', async ({ page }) => {
    await stubHostLoad(page, 0, 0, 0, 8.4);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('0 running');
    await expect(running).toHaveAttribute('data-signal-tone', 'mismatch');
    await expect(running).toHaveAttribute('data-signal-correlation', 'load-without-runs');
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(
      'Quiet consistency hint: host load is elevated without reported runs.',
    );

    await setTheme(page, 'dark');
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-host-load-mismatch-dark--mocked.png'),
      fullPage: false,
    });

    await setTheme(page, 'light');
    await running.hover();
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-host-load-mismatch-light--mocked.png'),
      fullPage: false,
    });
  });

  test('several reported runs with almost no load become the inverse quiet hint', async ({ page }) => {
    await stubHostLoad(page, 0, 3, 3, 0.3);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('3 running');
    await expect(running).toHaveAttribute('data-signal-tone', 'mismatch');
    await expect(running).toHaveAttribute('data-signal-correlation', 'runs-without-load');
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(
      'Quiet consistency hint: reported runs and host load may not correspond.',
    );
  });

  test('shows an explicit warning icon when telemetry and board leases diverge', async ({ page }) => {
    await stubHostLoad(page, 0, 2, 3, 4.2);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('2 running');
    await expect(page.getByTestId('status-bar-running-divergence')).toBeVisible();
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(
      'Board leases report 2 remote, but fresh host telemetry reports 3 active slots.',
    );

    await setTheme(page, 'dark');
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-running-divergence-dark--mocked.png'),
      fullPage: false,
    });
    await setTheme(page, 'light');
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-running-divergence-light--mocked.png'),
      fullPage: false,
    });
  });
});
