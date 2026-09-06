import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

const PROJECT = 'lane-presentation-fixture';
const WATCH_PATH = 'C:/fixtures/lane-presentation';
const TASK_ID = 'lane-presentation-human-review';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function task() {
  return {
    id: TASK_ID,
    taskKey: `${WATCH_PATH}::${TASK_ID}`,
    key: 'LANE-5',
    title: 'One lane presentation everywhere',
    state: '5-human-review',
    kind: 'task',
    mode: 'coding',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.2-codex',
    createdAt: '2026-09-06T08:00:00Z',
    lastActivity: '2026-09-06T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/5-human-review/${TASK_ID}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    tags: [],
  };
}

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [task()], escalated: [], completed: [], archive: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { id: PROJECT, name: PROJECT, shortCode: 'LNE', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/workspaces**', route => json(route, []));
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/tasks', route => json(route, [task()]));
  await page.route('**/api/tasks/grouped**', route => json(route, grouped()));
  await page.route(`**/api/tasks/${TASK_ID}/session-events**`, route => json(route, {
    events: [], sessionChain: [], currentSessionId: null,
  }));
  await page.route(`**/api/tasks/${TASK_ID}/runs**`, route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false, runs: [],
  }));
  await page.route(`**/api/tasks/${TASK_ID}/screenshots**`, route => json(route, { screenshots: [] }));
  await page.route(`**/api/tasks/${TASK_ID}/plan**`, route => json(route, null));
  await page.route(`**/api/projects/${PROJECT}/workbenches**`, route => json(route, {
    projectName: PROJECT, includesHistory: true, count: 0, items: [],
  }));
  await page.route(/\/api\/tasks\/lane-presentation-human-review(\?|$)/, route => json(route, {
    info: task(),
    promptMarkdown: '# Lane presentation parity',
    statusMarkdown: '# Status\n\n## What Was Done\n- Finished the lane presentation fixture.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: null,
  }));
  await page.route(`**/api/tasks/${TASK_ID}/pipeline**`, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null,
    executions: [],
    config: {},
    cost: null,
  }));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-09-06T09:00:00Z', snapshots: [] }));
  await page.route(/\/api\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
}

test('5-human-review uses one name and tone on board, task header, and Result', async ({ page }, testInfo) => {
  await installRoutes(page);
  await page.goto('/');
  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) mkdirSync(resultsDir, { recursive: true });

  const lane = page.getByTestId('lane-5-human-review').first();
  await expect(lane).toBeVisible();
  await expect(lane).toHaveAttribute('data-lane-tone', '--studio-lane-human-review');
  const laneTitle = page.getByTestId('lane-title-5-human-review').first();
  await expect(laneTitle).toHaveText('Human review');
  const boardTone = await laneTitle.evaluate(element => getComputedStyle(element).color);
  if (resultsDir) {
    await page.screenshot({ path: path.join(resultsDir, 'lane-presentation-board.png'), fullPage: true });
  }

  await lane.locator('[data-testid="task-card"], [data-testid="job-card"]')
    .filter({ hasText: 'One lane presentation everywhere' })
    .first()
    .click();

  const headerChip = page.getByTestId('studio-lane-select');
  await expect(headerChip).toBeVisible();
  await expect(headerChip.locator('option:checked')).toHaveText('Human review');
  await expect(headerChip).toHaveAttribute('data-lane-tone', '--studio-lane-human-review');

  const resultBadge = page.getByTestId('result-case-badge');
  await expect(resultBadge).toBeVisible();
  await expect(resultBadge).toHaveText('Human review');
  await expect(resultBadge).toHaveAttribute('data-lane-tone', '--studio-lane-human-review');

  const [headerTone, resultTone] = await Promise.all([
    headerChip.evaluate(element => getComputedStyle(element).color),
    page.getByTestId('result-case-dot').evaluate(element => getComputedStyle(element).backgroundColor),
  ]);
  expect(headerTone).toBe(boardTone);
  expect(resultTone).toBe(boardTone);
  await expect(page.locator('app-error-dialog')).toHaveCount(0);

  if (resultsDir) {
    await page.screenshot({ path: path.join(resultsDir, 'lane-presentation-human-review.png'), fullPage: true });
  }
  await testInfo.attach('lane-presentation-human-review.png', {
    body: await page.screenshot({ fullPage: true }),
    contentType: 'image/png',
  });
});
