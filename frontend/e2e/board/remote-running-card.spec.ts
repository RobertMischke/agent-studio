import { test, expect, type Page } from '@playwright/test';

const PROJECT = 'remote-operations';
const WATCH_PATH = 'C:/fixtures/remote-operations';

const REMOTE_TASK = {
  id: 'AGT-2134-remote-proof',
  key: 'AGT-2134',
  taskKey: `${WATCH_PATH}::AGT-2134-remote-proof`,
  title: 'Remote runner board liveness proof',
  state: '3-progress',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-07-11T13:45:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/3-progress/AGT-2134-remote-proof`,
  lastActivity: '2026-07-11T13:46:00Z',
  sessionName: null,
  model: 'gpt-5.6-codex',
  execution: null,
  runner: {
    runnerId: 'agent-runner-01@linux-host',
    runnerName: 'agent-runner-01',
    hostname: 'linux-host',
    backendName: 'remote-runner',
    isRemote: true,
    leaseId: 'lease-remote-proof',
    fencingToken: 11,
    acquiredAt: '2026-07-11T13:45:00Z',
  },
  commit: null,
  commits: [],
  ownerClientId: 'local-default',
  tags: [],
};

const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [],
  progress: [REMOTE_TASK], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: {} }) }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'auto-continuous',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ snapshots: [] }) }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function openBoard(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible();
}

test.describe('Remote lease drives the board running state', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`remote card is visibly running without local activeExecution (${theme})`, async ({ page }, testInfo) => {
      await openBoard(page);
      await page.evaluate((selectedTheme) => {
        document.documentElement.dataset['studioTheme'] = selectedTheme;
        localStorage.setItem('atp.studio.theme', selectedTheme);
      }, theme);
      await page.setViewportSize({ width: 1600, height: 1000 });

      const card = page.getByTestId('task-card').filter({ hasText: REMOTE_TASK.title });
      await expect(card).toBeVisible();
      await expect(card).toHaveAttribute('data-running', 'true');

      const runner = card.getByTestId('task-card-runner');
      await expect(runner).toHaveAttribute('data-runner-kind', 'remote');
      await expect(runner).toContainText('remote · agent-runner-01');

      // Background feeds outside this fixture's board scope may surface a
      // generic error dialog. It is unrelated to the card projection and must
      // not cover the persisted visual proof.
      const errorClose = page.getByTestId('error-dialog-close');
      if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
      await page.evaluate(() => {
        document.querySelectorAll('app-error-dialog, vite-error-overlay').forEach((node) => node.remove());
      });

      const screenshot = await page.screenshot({ fullPage: false });
      await testInfo.attach(`remote-running-board-${theme}.png`, { body: screenshot, contentType: 'image/png' });
      if (process.env.JOB_RESULTS_DIR) {
        await page.screenshot({
          path: `${process.env.JOB_RESULTS_DIR}/remote-running-board-${theme}.png`,
          fullPage: false,
        });
      }
    });
  }
});
