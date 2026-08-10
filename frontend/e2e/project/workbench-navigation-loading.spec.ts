import { expect, test } from '@playwright/test';
import type { Page, Route, TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Dossier Navigation';
const PROJECT_SLUG = 'dossier-navigation';
const WORKBENCH_ID = 'navigation-dossier';
const WORKBENCH_KEY = 'DOS-W1';
const WATCH_PATH = 'C:/evidence/dossier-navigation';
const LOAD_REASON = 'Dossier file is missing from the project repository.';
const VIEWER_RESPONSE_LATENCY_MS = 350;

const WORKBENCH = {
  id: WORKBENCH_ID,
  key: WORKBENCH_KEY,
  title: 'Navigation dossier',
  summary: 'A deterministic dossier for navigation regression coverage.',
  status: 'decision-pending',
  phase: 'decision-ready',
  updatedAtUtc: '2026-08-10T08:00:00Z',
  entryPath: 'docs/operations/navigation-dossier/index.html',
  valid: true,
  error: null,
  sourceTaskKeys: [],
  relatedTaskKeys: [],
  openDecisionCount: 1,
};

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const root = resolve(process.env['JOB_RESULTS_DIR'] ?? testInfo.outputDir);
  mkdirSync(root, { recursive: true });
  return resolve(root, fileName);
}

async function installMocks(page: Page, viewerFailure = false): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths', route => json(route, [{
    name: PROJECT,
    path: WATCH_PATH,
    rootPath: WATCH_PATH,
    repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'ws-dossier-navigation',
    displayName: 'Evidence',
    sortOrder: 0,
    isDefault: true,
    projects: [{
      id: 'project-dossier-navigation',
      displayName: PROJECT,
      shortCode: 'DOS',
      workspaceId: 'ws-dossier-navigation',
      storageLocation: WATCH_PATH,
      sortOrder: 0,
      archived: false,
      urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-10T08:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-10T08:00:00Z', sessions: [],
  }));
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, route => json(route, {
    models: [], source: 'dossier-navigation-evidence',
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'codex', model: 'gpt-5', thinkingLevel: null,
  }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/tasks/grouped**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  }));
  await page.route('**/api/workbenches**', route => json(route, {
    projectName: null,
    count: 1,
    currentCount: 1,
    historyCount: 0,
    items: [{ projectName: PROJECT, workbench: WORKBENCH }],
  }));
  await page.route(
    new RegExp(`/api/projects/${encodeURIComponent(PROJECT)}/workbenches(?:\\?.*)?$`),
    route => json(route, {
      projectName: PROJECT,
      includesHistory: new URL(route.request().url()).searchParams.get('history') === 'true',
      count: 1,
      items: [WORKBENCH],
    }),
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`,
    async route => {
      // Keep the request pending beyond the shared loading-surface threshold.
      // This proves an inactive sibling placeholder cannot appear while the
      // actual viewer request is still in flight.
      await new Promise(resolve => setTimeout(resolve, VIEWER_RESPONSE_LATENCY_MS));
      return viewerFailure
        ? json(route, { error: LOAD_REASON }, 404)
        : json(route, {
          workbench: WORKBENCH,
          html: '<!doctype html><html><head><style>:root{color-scheme:light dark}body{margin:0;padding:24px;background:Canvas;color:CanvasText}</style></head><body><main><h1>Navigation dossier</h1><p data-testid="dossier-body">Loaded through the shared viewer.</p></main></body></html>',
          branch: 'develop',
          revision: '1234567890abcdef',
          workingTreeModified: false,
          fingerprint: 'a'.repeat(64),
        });
    },
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_KEY}/references`,
    route => json(route, {
      projectName: PROJECT,
      workbenchKey: WORKBENCH_KEY,
      workbenchId: WORKBENCH_ID,
      legacyTaskKeys: [],
      items: [],
    }),
  );
}

test('central dossier list opens the viewer and ends every loading surface', async ({ page }, testInfo) => {
  await installMocks(page);
  await page.goto('/#/workbenches');

  const item = page.getByTestId(`workbench-overview-item-${PROJECT}-${WORKBENCH_ID}`);
  await expect(item).toBeVisible();
  await page.getByTestId(`workbench-overview-full-${PROJECT}-${WORKBENCH_ID}`).click();

  await expect(page).toHaveURL(new RegExp(`/projects/${PROJECT_SLUG}/workbenches/${WORKBENCH_ID}`));
  await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible();
  await expect(page.getByTestId('workbench-viewer-loading')).toHaveCount(0);
  await expect(page.getByTestId('loading-surface-list')).toHaveCount(0);
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .getByTestId('dossier-body')).toBeVisible();
  await expect(page.getByTestId('error-dialog')).toHaveCount(0);

  await setTheme(page, 'light');
  await page.screenshot({
    path: evidencePath(testInfo, 'dossier-list-to-viewer-light--mocked.png'),
    fullPage: true,
  });
});

test('direct dossier route loads and ends every loading surface', async ({ page }, testInfo) => {
  await installMocks(page);
  await page.goto(`/#/projects/${PROJECT_SLUG}/workbenches/${WORKBENCH_ID}`);

  await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible();
  await expect(page.getByTestId('workbench-viewer-loading')).toHaveCount(0);
  await expect(page.getByTestId('loading-surface-list')).toHaveCount(0);
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .getByTestId('dossier-body')).toBeVisible();
  await expect(page.getByTestId('error-dialog')).toHaveCount(0);

  await setTheme(page, 'dark');
  await page.screenshot({
    path: evidencePath(testInfo, 'dossier-direct-route-dark--mocked.png'),
    fullPage: true,
  });
});

test('failed dossier load replaces the indicator with the backend reason', async ({ page }, testInfo) => {
  await installMocks(page, true);
  await page.goto(`/#/projects/${PROJECT_SLUG}/workbenches/${WORKBENCH_ID}`);

  const error = page.getByTestId('workbench-viewer-error');
  await expect(error).toBeVisible();
  await expect(error).toContainText(LOAD_REASON);
  await expect(page.getByTestId('workbench-viewer-loading')).toHaveCount(0);
  await expect(page.getByTestId('loading-surface-list')).toHaveCount(0);
  await expect(page.getByTestId('error-dialog')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `dossier-load-error-${theme}--mocked.png`),
      fullPage: true,
    });
  }
});
