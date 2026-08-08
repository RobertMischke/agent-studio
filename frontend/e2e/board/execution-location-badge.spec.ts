import { expect, test, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const PROJECT = 'Execution ownership';
const WATCH_PATH = '/fixtures/execution-ownership';
const RESULTS = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : join(process.cwd(), '..', 'results');

function location(
  state: string,
  runnerId: string | null,
  kind: 'local' | 'remote' | 'none',
  historical = false,
  lastRejection?: {
    code: string;
    runnerId: string;
    runnerName: string;
    reason: string;
    rejectedAtUtc: string;
  },
) {
  return {
    state, executionKind: kind, runnerId, clientId: runnerId,
    hostDisplayName: kind === 'remote' ? runnerId : 'Local workstation',
    configuredRunnerId: kind === 'local' ? 'agent-runner-01' : 'agent-runner-01',
    startedAt: '2026-07-12T06:00:00Z', lastHeartbeat: '2026-07-12T06:02:00Z',
    lastActivityAt: '2026-07-12T06:02:03Z', processId: kind === 'local' ? 321 : 654,
    sessionId: `session-${runnerId ?? 'recovery'}`, branch: `task/${runnerId ?? 'recovering'}`,
    worktreePath: `${WATCH_PATH}/${runnerId ?? 'recovering'}`,
    connectionState: state === 'remote-disconnected' ? 'disconnected' : 'connected',
    leaseState: kind === 'remote' ? 'active' : 'local-process',
    trustReason: kind === 'remote' ? 'The task server holds the fenced run lease.' : 'The local CLI registry reports a live process.',
    historical, lastRejection,
  };
}

function task(
  id: string,
  title: string,
  executionLocation: ReturnType<typeof location>,
  state = '3-progress',
) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, key: id.toUpperCase(), title, state, order: 1,
    agent: 'codex', cliType: 'codex', createdAt: '2026-07-12T06:00:00Z', watchPath: WATCH_PATH,
    projectName: PROJECT, folderPath: `${WATCH_PATH}/${id}`, lastActivity: '2026-07-12T06:02:03Z',
    sessionName: `session-${id}`, model: 'gpt-5.6-codex', useOwnSession: null, lastUsage: null,
    commit: null, ownerClientId: 'local-default', tags: [], executionLocation,
  };
}

const initialTasks = [
  task('local-one', 'Local execution', location('local-running', 'stable@local', 'local')),
  task('remote-one', 'Remote execution', location('remote-running', 'agent-runner-01', 'remote')),
  task('remote-two', 'Second remote slot', location('remote-running', 'agent-runner-02', 'remote')),
];

const rejectedReadyTask = task(
  'remote-rejected',
  'Remote dispatch is visibly blocked',
  location('awaiting-remote', 'agent-runner-01', 'remote', false, {
    code: 'repository-url-missing',
    runnerId: 'agent-runner-01',
    runnerName: 'agent-runner-01',
    reason: 'project has no repositoryUrl',
    rejectedAtUtc: '2026-08-08T07:30:00Z',
  }),
  '2-ready',
);

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page, currentTasks: () => typeof initialTasks): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const taskDetail = new URL(url).pathname.match(/^\/api\/tasks\/([^/]+)$/);
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/auth/status')) return json(route, {
      profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
    });
    if (route.request().method() === 'GET' && taskDetail && !['archive', 'grouped'].includes(taskDetail[1])) {
      const info = currentTasks().find(item => item.id === decodeURIComponent(taskDetail[1]));
      return json(route, {
        info, promptMarkdown: null, promptHistory: [], titleHistory: [], statusMarkdown: null,
        contextUsage: null, log: [], summaryState: null, reviewEvidence: [],
      });
    }
    if (url.includes('/api/tasks/grouped')) return json(route, {
      backlog: [], preparation: [], orchestratorPrep: [],
      ready: currentTasks().filter(item => item.state === '2-ready'),
      progress: currentTasks().filter(item => item.state === '3-progress'),
      failedPickup: [],
      codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
    });
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, currentTasks());
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/clients')) return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/runner/queue-starvation')) {
      const waiting = currentTasks().filter(item => item.state === '2-ready');
      return json(route, {
        active: waiting.length > 0,
        waitingTaskCount: waiting.length,
        availableSlots: waiting.length > 0 ? 8 : 0,
        thresholdMinutes: 30,
        observedAt: '2026-08-08T13:00:00Z',
        oldestEnteredLaneAt: waiting.length > 0 ? '2026-08-08T07:30:00Z' : null,
        items: waiting.map(item => ({
          taskId: item.id,
          taskKey: item.taskKey,
          projectName: item.projectName,
          title: item.title,
          enteredLaneAt: '2026-08-08T07:30:00Z',
          waitingMinutes: 330,
          lastRejection: item.executionLocation.lastRejection,
        })),
      });
    }
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

test('shows each concurrent task owner and limits warnings to the stale remote run', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await page.clock.install();
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  let liveTasks = initialTasks;
  await installRoutes(page, () => liveTasks);
  await page.goto('/?includeFixtures=true');
  // The broad mocked API returns empty values for shell features outside this
  // spec. Suppress the dev-only global diagnostic overlay so persisted visual
  // evidence shows the execution cards under test, not unrelated fixture noise.
  await page.addStyleTag({ content: '.dialog__overlay { display: none !important; }' });

  const badges = page.getByTestId('execution-location-badge');
  await expect(badges).toHaveCount(3);
  await expect(badges.nth(0)).toContainText('Local');
  await expect(badges.nth(1)).toContainText('Host · agent-runner-01');
  await expect(badges.nth(2)).toContainText('Host · agent-runner-02');
  await expect(page.locator('[data-execution-state="remote-running"]')).toHaveCount(2);
  await expect(page.locator('[data-execution-state="remote-disconnected"]')).toHaveCount(0);

  liveTasks = initialTasks.map(item => item.id === 'remote-one'
    ? { ...item, executionLocation: location('remote-disconnected', 'agent-runner-01', 'remote') }
    : item);
  await page.clock.fastForward(30_100);
  await expect(page.locator('[data-execution-state="remote-disconnected"]')).toHaveCount(1);
  await expect(page.locator('[data-execution-state="remote-running"]')).toHaveCount(1);
  await expect(page.locator('[data-execution-state="local-running"]')).toHaveCount(1);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.clock.runFor(100);
    await dismissDevErrorDialog(page);
    await page.evaluate(() => {
      document.querySelector('vite-error-overlay')?.remove();
      document.querySelector('ng-error-overlay')?.remove();
    });
    await expect(page.locator('[data-testid="error-dialog-overlay"]:visible')).toHaveCount(0);
    await expect(page.getByTestId('lane-3-progress')).toBeVisible();
    await page.screenshot({ path: join(RESULTS, `execution-location--${theme}.png`), fullPage: true });
  }

  liveTasks = initialTasks.map(item => item.id === 'remote-one'
    ? { ...item, executionLocation: location('remote-running', 'agent-runner-01', 'remote') }
    : item);
  await page.clock.fastForward(30_100);
  await expect(page.locator('[data-execution-state="remote-disconnected"]')).toHaveCount(0);
  await expect(page.locator('[data-execution-state="remote-running"]')).toHaveCount(2);
});

test('shows a durable remote refusal on the card and the starvation banner', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page, () => [rejectedReadyTask]);
  await page.goto('/?includeFixtures=true');
  await page.addStyleTag({ content: '.dialog__overlay { display: none !important; }' });

  const card = page.getByTestId('task-card').filter({ hasText: rejectedReadyTask.title });
  const rejection = card.getByTestId('remote-dispatch-rejection');
  await expect(rejection).toContainText('Runner agent-runner-01 rejected:');
  await expect(rejection).toContainText('project has no repositoryUrl');

  const banner = page.getByTestId('remote-queue-starvation-banner');
  await expect(banner).toContainText('1 task is waiting despite free Runner capacity.');
  await expect(banner).toContainText('8 slots are available.');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await expect(rejection).toBeVisible();
    await page.screenshot({
      path: join(RESULTS, `remote-refusal-starvation-${theme}--mocked.png`),
      fullPage: false,
    });
  }
});
