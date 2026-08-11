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
  authorityAttempts = 0,
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
  const activeProjectNames = [...local, ...remote].map(task => task.projectName);
  const projectNames = Array.from(
    { length: 16 },
    (_, index) => activeProjectNames[index] ?? `idle-project-${index + 1}`,
  );
  const runnerProjects = Object.fromEntries(projectNames.map((projectName, index) => [
    projectName,
    {
      projectName,
      mode: index < 8 ? 'auto-continuous' : 'manual',
      activeJobId: null,
      activeExecution: null,
      queuedJobIds: [],
    },
  ]));

  await page.route('**/api/auth/status', json({
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));
  await page.route('**/api/environment**', json({ isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths', json(projectNames.map((name, index) => ({
    name,
    path: `/mock/project-${index + 1}`,
    rootPath: `/mock/project-${index + 1}`,
  }))));
  await page.route('**/api/tasks/grouped', json({
    preparation: [],
    ready: [],
    progress: [...local, ...remote],
    review: [],
    completed: [],
    archive: [],
  }));
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/runner/status', json({ projects: runnerProjects }));
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
      activeSlots: telemetrySlots,
    }],
    findings: [],
  }));
  const activeAttempts = Array.from({ length: authorityAttempts }, (_, index) => ({
    kind: index < 4 ? 'review' : 'coding',
    attemptId: `attempt-${index + 1}`,
    taskKey: `AGT-RESTART-${index + 1}`,
    leaseId: `lease-authority-${index + 1}`,
    fence: index + 1,
    authorityEpoch: 2,
    leaseInstanceId: index < 4 ? 'review-host:1' : 'coding-host:1',
    observedAt: now,
    requestedTtlSeconds: 120,
    phase: 'running',
    expiresAt: new Date(Date.now() + 120_000).toISOString(),
    projectId: index < 3 ? 'Agent Studio' : 'Quality Studio',
  }));
  await page.route('**/api/v1/management/remote-hosts', json(authorityAttempts === 0 ? [] : [{
    runnerId: 'agent-runner-01',
    name: 'agent-runner-01',
    hostId: 'agent-runner-01',
    instanceId: 'agent-runner-01:restart',
    runnerVersion: '1.0.0',
    protocolVersion: 2,
    status: 'active',
    registeredAt: now,
    lastSeenAt: now,
    hostAdmission: {
      hostId: 'agent-runner-01',
      admissionState: 'open',
      automaticDrainReason: null,
      automaticDrainAt: null,
      operatorDrainReason: null,
      operatorDrainAt: null,
    },
    capabilities: [],
    telemetry: null,
    runtimeCapacity: {
      hostId: 'agent-runner-01',
      maxParallelism: 8,
      targetLoadPercent: 85,
      rampStrategy: 'balanced',
      version: 2,
      updatedAt: now,
    },
    effectiveMaxParallelism: 8,
    runtimeCapacityAppliedAt: now,
    runtimeCapacityAppliedVersion: 2,
    activeAttempts,
  }]));
}

test.describe('Status bar execution-host load companion signal', () => {
  test.use({ serviceWorkers: 'block' });

  test('corresponding run count and load share the existing pulse point', async ({ page }) => {
    await stubHostLoad(page, 1, 3, 3, 7.2);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('1 local · 3 remote');
    await expect(page.getByTestId('status-bar').getByText('8/16 auto')).toBeVisible();
    await expect(running).toHaveAttribute('data-signal-tone', 'working');
    await expect(running).toHaveAttribute('data-signal-correlation', 'consistent');
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('Open execution hosts');
    await expect(page.getByTestId('cac-tooltip')).toContainText('Execution host load 7.2 / 12 cores (60%)');
    await expect(page.getByTestId('cac-tooltip')).toContainText('3 active execution slots');

    await setTheme(page, 'light');
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-runners-both-positive-light--mocked.png'),
      fullPage: false,
    });
  });

  test('click opens Execution Hosts management', async ({ page }) => {
    await stubHostLoad(page, 2, 0, 0, 3.6);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('2 local');
    await expect(running).not.toContainText('remote');
    await running.click();

    await expect(page).toHaveURL(/#\/workspace\/settings\/execution-hosts(?:&|$)/);
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();
    await expect(page.getByTestId('remote-hosts-panel').getByRole('heading', { level: 2 }))
      .toHaveText('Execution Hosts');
  });

  test('high load without runs becomes a quiet hint in both themes', async ({ page }) => {
    await stubHostLoad(page, 0, 0, 0, 8.4);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('no runners');
    await expect(running).not.toContainText('local');
    await expect(running).not.toContainText('remote');
    await expect(running).toHaveAttribute('data-signal-tone', 'mismatch');
    await expect(running).toHaveAttribute('data-signal-correlation', 'load-without-runs');
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(
      'Quiet consistency hint: host load is elevated without reported runs.',
    );

    await setTheme(page, 'dark');
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-runners-none-dark--mocked.png'),
      fullPage: false,
    });

    await setTheme(page, 'light');
    await running.hover();
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-runners-none-light--mocked.png'),
      fullPage: false,
    });
  });

  test('several reported runs with almost no load become the inverse quiet hint', async ({ page }) => {
    await stubHostLoad(page, 0, 5, 5, 0.3);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('5 remote');
    await expect(running).not.toContainText('local');
    await expect(running).toHaveAttribute('data-signal-tone', 'mismatch');
    await expect(running).toHaveAttribute('data-signal-correlation', 'runs-without-load');
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(
      'Quiet consistency hint: reported runs and host load may not correspond.',
    );

    await setTheme(page, 'dark');
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-runners-remote-only-dark--mocked.png'),
      fullPage: false,
    });
  });

  test('re-adopted server authority restores the footer and host slots before board leases reload', async ({ page }) => {
    await stubHostLoad(page, 0, 0, 5, 4.2, 5);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('5 remote');
    await expect(page.getByTestId('status-bar-running-divergence')).not.toBeVisible();
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(
      'Server authority and host telemetry agree on 5 remote runs.',
    );

    await running.click();
    const card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(card).toContainText('5 active / 3 free / 8 total');
    await expect(card).toContainText('Agent Studio');
    await expect(card).toContainText('Quality Studio');

    await setTheme(page, 'dark');
    await page.screenshot({
      path: join(RESULTS_DIR, 'status-bar-re-adopted-attempts-dark--mocked.png'),
      fullPage: false,
    });
  });

  test('shows an explicit warning icon when telemetry and board leases diverge', async ({ page }) => {
    await stubHostLoad(page, 0, 2, 3, 4.2);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('2 remote');
    await expect(page.getByTestId('status-bar-running-divergence')).toBeVisible();
    await running.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(
      'Server authority reports 2 remote, but fresh host telemetry reports 3 active slots.',
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
