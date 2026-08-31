import { expect, test, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const ALPHA = 'Context Alpha';
const BETA = 'Context Beta';
const ALPHA_WATCH_PATH = '/tmp/context-alpha';
const BETA_WATCH_PATH = '/tmp/context-beta';

const TASK = {
  id: 'all-projects-context-task',
  key: 'CTX-101',
  displayKey: 'CTX-101',
  taskKey: `${ALPHA_WATCH_PATH}::all-projects-context-task`,
  title: 'Keep the cross-project board in scope',
  state: '2-ready',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-08-29T08:00:00Z',
  watchPath: ALPHA_WATCH_PATH,
  projectName: ALPHA,
  folderPath: `${ALPHA_WATCH_PATH}/2-ready/all-projects-context-task`,
  lastActivity: '2026-08-29T08:00:00Z',
  sessionName: null,
  model: null,
  useOwnSession: null,
  lastUsage: null,
  execution: null,
  commit: null,
  commits: [],
  ownerClientId: 'local-default',
  tags: [],
};

const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [TASK], progress: [], failedPickup: [], codeNotComplete: [],
  review: [], autoReview: [], humanReview: [], escalated: [],
  completed: [], archive: [],
};

const DETAIL = {
  info: TASK,
  promptMarkdown: '# Keep the cross-project board in scope',
  promptHistory: [],
  titleHistory: [],
  statusMarkdown: null,
  contextUsage: null,
  log: [],
  summaryState: null,
  reviewEvidence: [],
};

function evidencePath(testInfo: TestInfo, name: string): string {
  const root = process.env['JOB_RESULTS_DIR']?.trim()
    ? path.resolve(process.env['JOB_RESULTS_DIR'])
    : testInfo.outputDir;
  fs.mkdirSync(root, { recursive: true });
  return path.join(root, name);
}

async function installRoutes(page: Page, detailRequests: string[]): Promise<void> {
  await page.route('**/api/**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));

  await page.route('**/api/tasks/grouped**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));

  await page.route(/\/api\/tasks\/all-projects-context-task(\?|$)/, route => {
    detailRequests.push(route.request().url());
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(DETAIL) });
  });

  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([
      { name: ALPHA, path: ALPHA_WATCH_PATH, rootPath: ALPHA_WATCH_PATH },
      { name: BETA, path: BETA_WATCH_PATH, rootPath: BETA_WATCH_PATH },
    ]),
  }));

  await page.route('**/api/runner/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ projects: {} }),
  }));

  await page.route('**/api/auth/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
}

test.describe('task detail opened from the All-projects board', () => {
  test('keeps the workspace scope and returns to that board on close', async ({ page }, testInfo) => {
    const detailRequests: string[] = [];
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }));
      localStorage.setItem('activeProjects', '[]');
    });
    await installRoutes(page, detailRequests);

    await page.goto('/?includeFixtures=true');
    await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
    const projectPicker = page.getByTestId('studio-project-picker-trigger');
    await expect(projectPicker).toContainText('All projects');
    await page.screenshot({
      path: evidencePath(testInfo, 'all-projects-task-context--before--mocked.png'),
      fullPage: false,
    });

    await page.locator('[data-testid="lane-2-ready"] app-job-card', { hasText: TASK.title })
      .first()
      .click();

    await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('detail-back')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(TASK.title, { exact: true }).first()).toBeVisible();
    await page.screenshot({
      path: evidencePath(testInfo, 'all-projects-task-context--after--mocked.png'),
      fullPage: false,
    });

    // The task's project is a request-local data handle. It must not become
    // the global project selection rendered by the titlebar and board.
    await expect.poll(() => detailRequests.length).toBeGreaterThan(0);
    expect(new URL(detailRequests[0]).searchParams.get('project')).toBe(ALPHA);
    await expect(projectPicker).toContainText('All projects');
    await expect.poll(() => page.evaluate(() => localStorage.getItem('activeProjects')))
      .toBe('[]');

    await page.getByTestId('detail-back').click();

    await expect(page.getByTestId('studio-board')).toBeVisible();
    await expect(page.locator(`[data-testid="studio-tab-task:${TASK.taskKey}"]`)).toHaveCount(0);
    await expect(page.locator('[data-testid="studio-tab-board:__all__"]')).toHaveClass(/studio-tab--active/);
    await expect(projectPicker).toContainText('All projects');
  });
});
