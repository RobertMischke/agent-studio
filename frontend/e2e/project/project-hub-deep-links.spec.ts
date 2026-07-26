import { expect, test, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const PROJECT_ID = 'PROJ-900';
const PROJECT_NAME = 'Durable Links';
const RESULTS_DIR = process.env.PROJECT_HUB_DEEP_LINK_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'test-results', 'project-hub-deep-links');

const project = {
  id: PROJECT_ID,
  displayName: PROJECT_NAME,
  shortCode: 'DL',
  workspaceId: 'WS-LINKS',
  storageLocation: '/mock/tasks/durable-links',
  rootPath: '/mock/repos/durable-links',
  repositoryPath: '/mock/repos/durable-links',
  sortOrder: 0,
  archived: false,
  urls: [],
};

async function json(route: Route, body: unknown): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  // Register the broad fallback first; Playwright gives later routes priority.
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'WS-LINKS',
    displayName: 'Link Workspace',
    sortOrder: 0,
    isDefault: true,
    projects: [project],
  }]));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: PROJECT_NAME,
    path: project.storageLocation,
    rootPath: project.rootPath,
  }]));
  await page.route('**/api/tasks/grouped**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  }));
  await page.route(/\/api\/runner\/status(?:\?|$)/, route => json(route, { projects: {} }));
  await page.route(/\/api\/projects\/[^/]+\/snapshot(?:\?|$)/, route => json(route, {
    project: PROJECT_NAME,
    capturedAt: '2026-07-22T12:00:00Z',
    paths: {
      path: project.storageLocation,
      rootPath: project.rootPath,
      repositoryPath: project.repositoryPath,
    },
    settings: {
      autoCommit: true,
      crashRecoveryEnabled: true,
      autoPushStrategy: 'on-completed',
      runnerMode: 'manual',
      orchestratorModel: null,
    },
    runnerStatus: null,
    orchestratorLogTail: [],
    orchestratorSession: null,
    reviewDecisionsPending: [],
    runnerPendingDecisions: [],
    publishTargets: [],
    queueHealth: {
      severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [],
    },
  }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-07-22T12:00:00Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-07-22T12:00:00Z', sessions: [],
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null,
  }));
  await page.route(/\/api\/projects\/Durable%20Links\/wiki\/grading\/status$/, route =>
    json(route, { status: null }));
  await page.route(/\/api\/projects\/Durable%20Links\/wiki\/tree$/, route => json(route, {
    projectName: PROJECT_NAME,
    baseDir: '/mock/repos/durable-links/docs',
    exists: true,
    root: [{
      name: 'concepts',
      title: 'concepts',
      relPath: 'concepts',
      type: 'folder',
      children: [{
        name: 'overview.md',
        title: 'Routing overview',
        relPath: 'concepts/overview.md',
        type: 'md',
        children: [],
      }],
    }],
  }));
  await page.route(/\/api\/projects\/Durable%20Links\/wiki\/pulse(?:\?|$)/, route => json(route, {
    projectName: PROJECT_NAME,
    baseDir: '/mock/repos/durable-links/docs',
    exists: true,
    generatedAtUtc: '2026-07-22T12:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true,
      reason: null,
      overallGrade: 'Fresh',
      areas: [],
      counts: { fresh: 1, aging: 0, stale: 0, graded: 1 },
    },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  }));
  await page.route(/\/api\/projects\/Durable%20Links\/wiki\/files\/concepts\/overview\.md$/, route =>
    json(route, { relPath: 'concepts/overview.md', content: '# Stable routing overview' }));
  await page.route(/\/api\/projects\/Durable%20Links\/wiki\/history\/concepts\/overview\.md$/, route =>
    json(route, {
      relPath: 'concepts/overview.md',
      model: null,
      metadata: {
        model: null, updatedAt: null, reason: null, taskKey: null,
        status: null, runCount: null, hasFrontmatter: false,
      },
      commits: [],
    }));
}

async function expectRail(page: Page, rail: string): Promise<void> {
  await expect(page.getByTestId(`project-shell-panel-${rail}`)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId(`project-shell-rail-${rail}`)).toHaveAttribute('aria-current', 'page');
}

async function expectRoute(page: Page, route: string): Promise<void> {
  await expect.poll(() => page.evaluate(() =>
    window.location.hash.slice(1).split('&').find(segment => segment.startsWith('/')),
  )).toBe(route);
}

test.beforeEach(async ({ page }) => {
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  await installRoutes(page);
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.removeItem('atp.studio.tabs.v1');
  });
});

test('an id-based Project Hub URL survives reload and rail history', async ({ page }) => {
  await page.goto(`/#/projects/${PROJECT_ID}/settings`);
  await expectRail(page, 'settings');
  await expectRoute(page, `/projects/${PROJECT_ID}/settings`);

  await page.reload();
  await expectRail(page, 'settings');

  await page.getByTestId('project-shell-rail-project-urls').click();
  await expectRail(page, 'project-urls');
  await expectRoute(page, `/projects/${PROJECT_ID}/project-urls`);

  await page.goBack();
  await expectRail(page, 'settings');
  await expectRoute(page, `/projects/${PROJECT_ID}/settings`);

  await page.screenshot({
    path: path.join(RESULTS_DIR, 'project-hub-stable-deep-link.png'),
    fullPage: false,
  });
});

test('a legacy Wiki page route redirects to the id and restores the exact page', async ({ page }) => {
  await page.goto('/#/projects/durable-links/wiki?page=concepts%2Foverview.md');

  await expectRail(page, 'wiki');
  await expectRoute(page, `/projects/${PROJECT_ID}/wiki?page=concepts%2Foverview.md`);
  await expect(page.getByTestId('project-wiki-viewer-path'))
    .toContainText('concepts/overview.md');
  await expect(page.getByTestId('project-shell')).toHaveCount(1);
});
