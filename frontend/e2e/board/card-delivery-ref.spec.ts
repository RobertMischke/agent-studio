import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Delivery ref fixture';
const WATCH_PATH = '/fixtures/delivery-ref-card';
const DELIVERY_REF = 'runner/agent-runner-01/AGT-2220';
const RESULTS = process.env['JOB_RESULTS_DIR']
  ?? '/home/agent/runner-work/tasks/AGT-2718/results';

const repositoryCounts = [
  ['agent-studio', 5, 'develop', 'main'],
  ['runner', 4, 'main', 'main'],
  ['token-economy', 4, 'main', 'main'],
  ['chat', 3, 'main', 'main'],
  ['ai-patterns.dev', 2, 'main', 'main'],
  ['quality-studio', 1, 'main', 'main'],
  ['.github', 1, 'main', 'main'],
] as const;

const multiRepositoryTask = {
  id: 'externalization-sweep',
  taskKey: `${WATCH_PATH}::externalization-sweep`,
  key: 'AGT-2307',
  title: 'Externalization sweep',
  state: '5-human-review',
  order: 2,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-08-04T12:00:00Z',
  lastActivity: '2026-08-04T12:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/tasks/AGT-2307`,
  model: 'gpt-5.6-codex',
  execution: null,
  commit: null,
  codeActivityDetected: true,
  ownerClientId: 'local-default',
  tags: [],
  provenance: null,
  commits: repositoryCounts.flatMap(([repository, count, branch], repositoryIndex) =>
    Array.from({ length: count }, (_, commitIndex) => ({
      sha: `${repositoryIndex + 1}${commitIndex + 1}`.padEnd(40, 'a'),
      shortSha: `${repositoryIndex + 1}${commitIndex + 1}`.padEnd(7, 'a'),
      message: `[${repository}] delivered commit`,
      repository,
      branch,
      filesChanged: 1,
      files: [`${repository}/delivered-${commitIndex}.txt`],
      at: '2026-08-04T12:00:00Z',
      attribution: 'automatic',
    }))),
  integration: {
    status: 'integrated',
    deliveryRef: null,
    sha: null,
    integrationBranch: 'develop',
    detail: '20/20 attributed commits integrated across 7 repositories.',
    repositories: repositoryCounts.map(([repository, count, integrationBranch, releaseBranch], repositoryIndex) => ({
      repository,
      label: repository,
      integrationBranch,
      releaseBranch,
      commits: Array.from({ length: count }, (_, commitIndex) => ({
        sha: `${repositoryIndex + 1}${commitIndex + 1}`.padEnd(40, 'a'),
        onIntegrationBranch: true,
        onReleaseBranch: true,
      })),
      onIntegrationBranch: true,
      onReleaseBranch: true,
      detail: `${count}/${count} commits on ${integrationBranch} and ${releaseBranch}.`,
    })),
  },
};

const remoteReviewTask = {
  id: 'out-of-band-nur-mit-verifizierten-commits',
  taskKey: `${WATCH_PATH}::out-of-band-nur-mit-verifizierten-commits`,
  key: 'AGT-2220',
  title: 'Out-of-band nur mit verifizierten Commits',
  state: '5-human-review',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-07-22T15:00:00Z',
  lastActivity: '2026-07-30T00:07:44Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/tasks/AGT-2220`,
  model: 'gpt-5.6-codex',
  execution: null,
  commit: null,
  commits: [],
  codeActivityDetected: false,
  ownerClientId: 'local-default',
  tags: [],
  provenance: {
    branch: 'task/out-of-band-nur-mit-verifizierten-commits',
    base: null,
    transitions: [{
      lane: '5-human-review',
      atUtc: '2026-07-29T22:07:44Z',
      branchTip: null,
      workBranchHead: '9af1a848e3be1401340b3b15c9704ae1f87c9408',
    }],
    merge: null,
  },
  integration: {
    status: 'pending',
    deliveryRef: DELIVERY_REF,
    sha: null,
    integrationBranch: 'main',
    detail: `Delivery ref '${DELIVERY_REF}' is not yet integrated into main.`,
  },
};

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/auth/status')) {
      return json(route, {
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      });
    }
    if (url.includes('/api/tasks/grouped')) {
      return json(route, {
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        codeNotComplete: [],
        review: [],
        autoReview: [],
        humanReview: [remoteReviewTask, multiRepositoryTask],
        escalated: [],
        completed: [],
        archive: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) {
      return json(route, [remoteReviewTask, multiRepositoryTask]);
    }
    if (url.includes('/api/watch-paths')) {
      return json(route, [{
        name: PROJECT,
        path: WATCH_PATH,
        rootPath: WATCH_PATH,
        repositoryPath: WATCH_PATH,
      }]);
    }
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/clients')) {
      return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    }
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    return json(route, []);
  });
}

async function boot(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await dismissDevErrorDialog(page);
}

test.describe('Review card delivery ref projection', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`shows the remote delivery ref from card truth (${theme})`, async ({ page }, testInfo) => {
      await boot(page);
      await setTheme(page, theme);

      const card = page.getByTestId('task-card').filter({ hasText: 'AGT-2220' }).first();
      await expect(card).toBeVisible({ timeout: 15_000 });

      const integration = card.getByTestId('integration-status-badge');
      await expect(integration).toHaveAttribute('data-integration-status', 'pending');
      await expect(integration).toContainText('not integrated');
      await expect(integration).not.toContainText('no branch');

      const context = card.getByTestId('task-card-change-context');
      await expect(context).toContainText(DELIVERY_REF);
      await expect(context).toContainText('commit discovery pending');
      await expect(context).not.toContainText('main checkout');
      await expect(context).not.toContainText('no code changes');

      mkdirSync(RESULTS, { recursive: true });
      const screenshotPath = join(RESULTS, `board-delivery-ref-${theme}.png`);
      await card.screenshot({ path: screenshotPath });
      await testInfo.attach(`board-delivery-ref-${theme}.png`, {
        path: screenshotPath,
        contentType: 'image/png',
      });
    });
  }
});

test.describe('Multi-repository integration projection', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`renders all AGT-2307 repositories as integrated (${theme})`, async ({ page }, testInfo) => {
      await boot(page);
      await setTheme(page, theme);

      const card = page.getByTestId('task-card').filter({ hasText: 'AGT-2307' }).first();
      await expect(card).toBeVisible({ timeout: 15_000 });
      const integration = card.getByTestId('integration-status-badge');
      await expect(integration).toHaveCount(7);
      await expect(integration.filter({ hasText: 'agent-studio 5/5 develop and main' })).toHaveCount(1);
      await expect(integration.filter({ hasText: 'runner 4/4 main' })).toHaveCount(1);
      await expect(integration.filter({ hasText: '.github 1/1 main' })).toHaveCount(1);
      for (const chip of await integration.all()) {
        await expect(chip).toHaveAttribute('data-kind', 'integrated');
      }

      mkdirSync(RESULTS, { recursive: true });
      const screenshotPath = join(RESULTS, `board-multi-repository-integration-${theme}--mocked.png`);
      await card.screenshot({ path: screenshotPath });
      await testInfo.attach(`board-multi-repository-integration-${theme}--mocked.png`, {
        path: screenshotPath,
        contentType: 'image/png',
      });
    });
  }
});
