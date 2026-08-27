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
  await page.route('**/api/v1/management/remote-hosts', json([]));
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

  test('review workers own elevated host load and expose both plane ceilings', async ({ page }) => {
    await stubHostLoad(page, 0, 0, 4, 8.4);
    const now = new Date().toISOString();
    const telemetry = (clientId: string, activeSlots: number) => ({
      clientId,
      window: '1h',
      points: [{
        timestamp: now,
        cpuPercent: 72,
        load1: 4.2,
        load5: 4.2,
        load15: 4.2,
        memoryUsedBytes: 24_000_000_000,
        memoryTotalBytes: 64_000_000_000,
        swapInBytesPerSecond: 0,
        swapOutBytesPerSecond: 0,
        cpuStealPercent: 0,
        ioWaitPercent: 0,
        cpuCores: 6,
        activeSlots,
      }],
      findings: [],
    });
    const hostAdmission = {
      hostId: 'host-berlin',
      admissionState: 'open',
      automaticDrainReason: null,
      automaticDrainAt: null,
      operatorDrainReason: null,
      operatorDrainAt: null,
    };
    const capability = (key: 'executor:coding' | 'executor:review') => ({
      key,
      category: 'executor',
      advertisedStatus: 'ready',
      healthState: 'healthy',
      reason: null,
      advertisedAt: now,
      freshUntil: new Date(Date.now() + 60_000).toISOString(),
      isFresh: true,
      firstFailureAt: null,
      lastFailureAt: null,
      cooldownUntil: null,
      canaryClaimId: null,
      consecutiveFailures: 0,
      version: null,
      identity: null,
      detail: null,
      affectedClaims: [],
      recoveryHistory: [],
    });

    await page.unroute('**/api/clients');
    await page.route('**/api/clients', json([
      {
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: now, lastSeenAt: now, runnerGitStatus: 'ready',
        runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 8,
        runnerEffectiveMaxParallelism: 8,
      },
      {
        id: 'agent-runner-review-01', displayName: 'agent-runner-review-01', kind: 'service',
        registeredAt: now, lastSeenAt: now, runnerGitStatus: 'ready',
        runnerDaemonState: 'running', runnerActiveSlots: 4, runnerAvailableSlots: 2,
        runnerEffectiveMaxParallelism: 6,
      },
    ]));
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', json([
      {
        runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'host-berlin',
        instanceId: 'coding', runnerVersion: '1.2.0', protocolVersion: 2,
        status: 'active', registeredAt: now, lastSeenAt: now, hostAdmission,
        capabilities: [capability('executor:coding')], effectiveMaxParallelism: 8,
      },
      {
        runnerId: 'agent-runner-review-01', name: 'agent-runner-review-01', hostId: 'host-berlin',
        instanceId: 'review', runnerVersion: '1.2.0', protocolVersion: 2,
        status: 'active', registeredAt: now, lastSeenAt: now, hostAdmission,
        capabilities: [capability('executor:review')], effectiveMaxParallelism: 6,
      },
    ]));
    await page.unroute('**/api/clients/agent-runner-01/telemetry?window=1h');
    await page.unroute('**/api/clients/agent-runner-01/telemetry?window=14d');
    await page.route('**/api/clients/agent-runner-01/telemetry?window=*', json(telemetry('agent-runner-01', 0)));
    await page.route('**/api/clients/agent-runner-review-01/telemetry?window=*', json(telemetry('agent-runner-review-01', 4)));
    await page.route('**/api/runner/auto-review-queue', json({
      queueDepth: 7,
      activeJobs: 4,
      isStagnant: false,
      stagnantSince: null,
      stagnantThresholdMinutes: 20,
      drainRatePerMinute: 0.8,
      medianReviewDurationMs: 190_000,
      throughputWindowMinutes: 15,
      observedAt: now,
    }));

    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('coding 0/8');
    await expect(running).not.toContainText('no runners');
    await expect(running).toHaveAttribute('data-signal-correlation', 'consistent');
    const review = page.getByTestId('status-bar-review');
    await expect(review).toContainText('review 4/6 active');
    await expect(review).toContainText('7 waiting');
    await review.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('4 processing, 7 waiting');

    await setTheme(page, 'dark');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-plane-utilization-dark--mocked.png'),
    });
    await setTheme(page, 'light');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-plane-utilization-light--mocked.png'),
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

  test('shows an explicit warning icon when telemetry and board leases diverge', async ({ page }) => {
    await stubHostLoad(page, 0, 2, 3, 4.2);
    await page.goto('/');

    const running = page.getByTestId('status-bar-running');
    await expect(running).toContainText('2 remote');
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

  test('shows a calm repaired note and reserves the alarm for repair failure', async ({ page }) => {
    await stubHostLoad(page, 0, 0, 0, 0.4);
    await page.route('**/api/cli/local-capabilities', json({
      at: '2026-08-18T09:01:00Z',
      latestRepair: {
        detectedAt: '2026-08-18T09:00:00Z',
        completedAt: '2026-08-18T09:01:00Z',
        cliType: 'claude',
        packageName: '@anthropic-ai/claude-code',
        installState: 'missing-shim-package-present',
        outcome: 'repaired',
        cliVersionBefore: '2.1.231',
        cliVersionAfter: '2.1.234',
        detail: 'Reinstalled @anthropic-ai/claude-code and restored the claude shim.',
      },
      activeFailure: null,
      journalPath: 'C:/agent-taskboard/logs/cli-repairs.jsonl',
    }));
    await page.goto('/');

    const note = page.getByTestId('status-bar-cli-repair');
    await expect(note).toContainText('CLI repaired at');
    await expect(note.getByTestId('status-bar-cli-repair-warning')).toHaveCount(0);
    await note.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('2.1.231 → 2.1.234');

    await setTheme(page, 'dark');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repaired-dark--mocked.png'),
    });
    await setTheme(page, 'light');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repaired-light--mocked.png'),
    });
  });
});
