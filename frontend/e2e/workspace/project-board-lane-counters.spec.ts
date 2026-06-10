import { test, expect, type Page, type Route } from '@playwright/test';

const PROJECT = 'Lane Counter Project';
const WATCH_PATH = 'C:/fixtures/lane-counter-project';

interface TaskFixture {
  id: string;
  taskKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  cliType: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: null;
  model: null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  ownerClientId: string;
  tags: string[];
}

function task(id: string, state: string, order: number): TaskFixture {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: id,
    state,
    order,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-06-09T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${id}`,
    lastActivity: '2026-06-09T08:00:00Z',
    sessionName: null,
    model: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ownerClientId: 'local-default',
    tags: [],
  };
}

const READY = [
  task('ready-one', '2-ready', 1),
  task('ready-two', '2-ready', 2),
  task('ready-three', '2-ready', 3),
];
const PROGRESS = [
  task('progress-one', '3-progress', 1),
  task('progress-two', '3-progress', 2),
];
const HUMAN_REVIEW = [
  task('review-one', '5-human-review', 1),
  task('review-two', '5-human-review', 2),
  task('review-three', '5-human-review', 3),
  task('review-four', '5-human-review', 4),
  task('review-five', '5-human-review', 5),
];

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: READY,
  progress: PROGRESS,
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [],
  humanReview: HUMAN_REVIEW,
  escalated: [],
  completed: [],
  archive: [task('archived', '7-archive', 1)],
};

const ALL_TASKS = [...READY, ...PROGRESS, ...HUMAN_REVIEW, ...GROUPED.archive];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.includes('/api/tasks/grouped') || url.includes('/api/jobs/grouped')) {
      return json(route, GROUPED);
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) {
      return json(route, ALL_TASKS);
    }
    if (url.includes('/api/watch-paths')) {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.includes('/api/workspaces')) {
      return json(route, [{
        id: 'ws-lane',
        displayName: 'Lane Workspace',
        sortOrder: 0,
        isDefault: true,
        color: null,
        createdAt: '2026-06-09T08:00:00Z',
        projects: [{
          id: 'PROJ-LANE',
          displayName: PROJECT,
          shortCode: 'LANE',
          workspaceId: 'ws-lane',
          color: null,
          cliDefault: null,
          modelDefault: null,
          sortOrder: 0,
          storageLocation: WATCH_PATH,
          archived: false,
          createdAt: '2026-06-09T08:00:00Z',
        }],
      }]);
    }
    if (url.includes('/api/runner/status')) {
      return json(route, {
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      });
    }
    if (url.includes('/api/runner/token-summary-aggregate')) return json(route, {});
    if (url.includes('/api/tags')) return json(route, []);
    if (url.includes('/api/clients')) return json(route, []);
    if (url.includes('/api/cli/usage')) return json(route, { at: '2026-06-09T08:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json(route, { at: '2026-06-09T08:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/environment')) {
      return json(route, { isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } });
    }
    if (url.includes('/api/agent-rules')) return json(route, []);
    return json(route, []);
  });
}

async function boot(page: Page): Promise<void> {
  await page.addInitScript((project) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: project }],
      activeKey: `board:${project}`,
    }));
    localStorage.setItem('atp.studio.explorer.expanded', JSON.stringify([project]));
    localStorage.removeItem('atp.studio.explorerSections');
  }, PROJECT);
  await installRoutes(page);
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
}

test('Explorer Project Board row shows subtle live lane counters', async ({ page }) => {
  await boot(page);

  const board = page.getByTestId(`studio-explorer-project-board-${PROJECT}`);
  await expect(board).toBeVisible();
  await expect(board).toHaveAttribute('aria-label', 'Board, 3 ready, 2 in progress, 5 human review');
  await expect(page.getByTestId(`studio-explorer-project-board-counts-${PROJECT}`))
    .toHaveAttribute('aria-label', '3 ready, 2 in progress, 5 human review');
  await expect(page.getByTestId(`studio-explorer-project-board-count-ready-${PROJECT}`)).toHaveText('3');
  await expect(page.getByTestId(`studio-explorer-project-board-count-progress-${PROJECT}`)).toHaveText('2');
  await expect(page.getByTestId(`studio-explorer-project-board-count-human-review-${PROJECT}`)).toHaveText('5');

  const styles = await page.getByTestId(`studio-explorer-project-board-count-ready-${PROJECT}`).evaluate((el) => {
    const computed = getComputedStyle(el);
    return {
      color: computed.color,
      background: computed.backgroundColor,
      border: computed.borderColor,
    };
  });

  expect(styles.color).not.toBe(styles.background);
  expect(styles.border).not.toBe(styles.background);

  const screenshotPath = test.info().outputPath('project-board-lane-counters.png');
  await board.screenshot({ path: screenshotPath });
  await test.info().attach('project-board-lane-counters', {
    path: screenshotPath,
    contentType: 'image/png',
  });
});

test('each lane counter explains its lane via the canonical appTooltip', async ({ page }) => {
  await boot(page);

  await expect(page.getByTestId(`studio-explorer-project-board-counts-${PROJECT}`)).toBeVisible();

  const tip = page.getByTestId('app-tooltip');

  // Grey counter = Ready (2-ready).
  await page.getByTestId(`studio-explorer-project-board-count-ready-${PROJECT}`).hover();
  await expect(tip).toBeVisible({ timeout: 5_000 });
  await expect(tip).toHaveText(/Ready.*queued for a coding agent/);

  // Orange counter = In Progress (3-progress).
  await page.getByTestId(`studio-explorer-project-board-count-progress-${PROJECT}`).hover();
  await expect(tip).toBeVisible({ timeout: 5_000 });
  await expect(tip).toHaveText(/In Progress.*actively running/);

  // Green counter = Human Review (5-human-review).
  await page.getByTestId(`studio-explorer-project-board-count-human-review-${PROJECT}`).hover();
  await expect(tip).toBeVisible({ timeout: 5_000 });
  await expect(tip).toHaveText(/Human Review.*waiting for your review/);

  const tipShot = test.info().outputPath('project-board-lane-counter-tooltip.png');
  await tip.screenshot({ path: tipShot });
  await test.info().attach('project-board-lane-counter-tooltip', {
    path: tipShot,
    contentType: 'image/png',
  });
});
