import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Route Project';
const PROJECT_SLUG = 'route-project';
const WATCH_PATH = '/tmp/route-project';
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], codeNotComplete: [],
  review: [], autoReview: [], humanReview: [], escalated: [],
  completed: [], archive: [],
};

interface DossierItem {
  id: string;
  key: string;
  title: string;
  summary: string;
  status: 'active' | 'decision-pending' | 'decided' | 'documented' | 'archived';
  phase: 'testing' | 'decision-ready' | null;
  updatedAtUtc: string;
  entryPath: string;
  valid: true;
  error: null;
  sourceTaskKeys: string[];
  relatedTaskKeys: string[];
  openDecisionCount: number;
}

function dossier(
  id: string,
  key: string,
  title: string,
  status: DossierItem['status'],
  updatedAtUtc: string,
  openDecisionCount = 0,
): DossierItem {
  return {
    id,
    key,
    title,
    summary: `${title} keeps the list state deterministic for review.`,
    status,
    phase: status === 'decision-pending' ? 'decision-ready' : status === 'active' ? 'testing' : null,
    updatedAtUtc,
    entryPath: `docs/${id}/index.html`,
    valid: true,
    error: null,
    sourceTaskKeys: [],
    relatedTaskKeys: [],
    openDecisionCount,
  };
}

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const resultRoot = process.env['JOB_RESULTS_DIR']?.trim();
  const directory = resultRoot ? path.resolve(resultRoot) : testInfo.outputDir;
  fs.mkdirSync(directory, { recursive: true });
  return path.join(directory, fileName);
}

async function proxyApi(
  page: Page,
  projectName: string,
  projectItems: readonly DossierItem[],
  allItems: readonly { projectName: string; workbench: DossierItem }[],
): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (url.pathname === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/workspaces' || url.pathname === '/api/projects') return json([]);
    if (url.pathname === '/api/watch-paths') {
      return json([{ name: projectName, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.pathname === '/api/tasks/grouped') return json(EMPTY_GROUPED);
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/tasks' || url.pathname === '/api/tags'
        || url.pathname === '/api/clients' || url.pathname === '/api/clients/') return json([]);
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname === '/api/epics/completed/count') return json({ count: 0 });
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/runner/orchestrator-feed') return json({ entries: [] });
    if (url.pathname === '/api/orchestrator/sessions') return json({ sessions: [] });
    if (url.pathname === '/api/v1/management/remote-hosts') return json([]);
    if (/\/api\/bus\/[^/]+\/messages$/.test(url.pathname)) return json([]);
    if (url.pathname.startsWith('/api/runner/token-summary-aggregate')) {
      return json({
        projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
        totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: true,
        byModel: [], byProject: [], fetchedAt: '2026-08-11T10:00:00Z', disclaimer: '',
      });
    }
    if (url.pathname.startsWith('/api/workspace/tokens/timeline')) {
      return json({
        windowStart: '2026-08-10T10:00:00Z', windowEnd: '2026-08-11T10:00:00Z',
        windowHours: 24, bucketMinutes: 60, bucketCount: 0, cells: [], projects: [],
        fetchedAt: '2026-08-11T10:00:00Z', disclaimer: '',
      });
    }
    if (url.pathname.startsWith('/api/workspace/tokens/expensive-jobs')) return json({ jobs: [] });
    if (url.pathname.startsWith('/api/adhoc-usage')) {
      return json({
        calls: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0,
        cacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: true,
        bySource: [], byDay: [], byModel: [], logPath: '', logSizeBytes: 0,
        logModifiedAt: null, disclaimer: '',
      });
    }
    if (url.pathname === '/api/workbenches') {
      const requestedProject = url.searchParams.get('project');
      const items = requestedProject
        ? allItems.filter(item => item.projectName === requestedProject)
        : [...allItems];
      return json({
        projectName: requestedProject,
        count: items.length,
        currentCount: items.filter(item =>
          ['active', 'decision-pending', 'decided'].includes(item.workbench.status)).length,
        historyCount: items.filter(item =>
          ['archived', 'documented'].includes(item.workbench.status)).length,
        items,
      });
    }
    if (decodeURIComponent(url.pathname) === `/api/projects/${projectName}/workbenches`) {
      return json({
        projectName,
        includesHistory: url.searchParams.get('history') === 'true',
        count: projectItems.length,
        items: projectItems,
      });
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname)) {
      return json({ models: [], source: 'workbench-sort-filter-e2e' });
    }
    if (url.pathname === '/api/cli/quota') {
      return json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
    }
    if (url.pathname === '/api/cli/usage') {
      return json({ at: new Date().toISOString(), sessions: [] });
    }
    if (url.pathname === '/api/cli/maintenance-model') {
      return json({ cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null });
    }
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (route.request().method() === 'GET') return json({});
    return json({});
  });
}

test('Dossier sort and live filter round-trip on workspace and project overviews', async ({ page }, testInfo) => {
  test.setTimeout(150_000);
  const projectName = PROJECT;

  const projectItems = [
    dossier('decision-route', 'WB-40', 'Decision route', 'decision-pending', '2026-08-11T10:00:00Z', 3),
    dossier('beta-route', 'WB-2', 'Beta route', 'active', '2026-08-09T10:00:00Z'),
    dossier('accepted-route', 'WB-11', 'Accepted route', 'decided', '2026-08-10T10:00:00Z'),
    dossier('legacy-route', 'WB-1', 'Legacy route', 'archived', '2026-08-08T10:00:00Z'),
  ];
  const other = dossier('other-route', 'WB-3', 'Other project route', 'active', '2026-08-12T10:00:00Z');
  const allItems = [
    ...projectItems.map(workbench => ({ projectName, workbench })),
    { projectName: 'Zeta Lab', workbench: other },
  ];

  await page.addInitScript(() => {
    if (window.sessionStorage.getItem('workbench-sort-filter-e2e-initialized')) return;
    window.sessionStorage.setItem('workbench-sort-filter-e2e-initialized', 'true');
    window.sessionStorage.removeItem('atp.workbenches.overview.view.v1');
    window.localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
  });
  await proxyApi(page, projectName, projectItems, allItems);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/#/workbenches', { waitUntil: 'commit' });
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await expect(page.getByTestId('workbench-overview-controls')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId(`workbench-overview-item-${projectName}-decision-route`)).toBeVisible();

  const projectSort = page.getByTestId('workbench-sort-project');
  await projectSort.click();
  await expect(projectSort).toHaveAttribute('aria-label', 'Sort by Project, ascending');
  const currentItems = page.getByTestId('workbench-overview-sorted')
    .locator('[data-testid^="workbench-overview-item-"]');
  await expect(currentItems.first()).toHaveAttribute(
    'data-testid',
    `workbench-overview-item-${projectName}-decision-route`,
  );
  await projectSort.click();
  await expect(projectSort).toHaveAttribute('aria-label', 'Sort by Project, descending');
  await expect(currentItems.first()).toHaveAttribute(
    'data-testid',
    'workbench-overview-item-Zeta Lab-other-route',
  );
  await expect.poll(() => new URL(page.url()).hash)
    .toContain('view=sort%3Dproject%26dir%3Ddesc');

  await page.mouse.move(1360, 960);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `workbench-sort-filter-central--mocked-${theme}.png`),
      fullPage: true,
    });
  }

  const filter = page.getByTestId('workbench-filter-input');
  await filter.fill('WB-11');
  await expect(page.getByTestId(`workbench-overview-item-${projectName}-accepted-route`)).toBeVisible();
  await expect(page.getByTestId('workbench-filter-result-count')).toHaveText('1 of 5 matching');
  await expect(page.getByTestId('workbench-overview-item-Zeta Lab-other-route')).toHaveCount(0);
  await expect.poll(() => new URL(page.url()).hash).toContain('view=q%3DWB-11');

  await filter.fill('');
  await page.getByTestId('workbench-sort-default').click();
  await page.goto(`/#/projects/${PROJECT_SLUG}/workbenches`, { waitUntil: 'commit' });
  await expect(page.getByTestId('workbench-overview-scope')).toContainText(projectName);

  const keySort = page.getByTestId('workbench-sort-key');
  await keySort.click();
  await expect(keySort).toHaveAttribute('aria-label', 'Sort by Key, ascending');
  await expect(page.getByTestId('workbench-overview-sorted')
    .locator('[data-testid^="workbench-overview-item-"]').first())
    .toHaveAttribute('data-testid', `workbench-overview-item-${projectName}-beta-route`);

  await page.getByTestId('workbench-filter-input').fill('Beta');
  await expect(page.getByTestId(`workbench-overview-item-${projectName}-beta-route`)).toBeVisible();
  await expect(page.getByTestId('workbench-filter-result-count')).toHaveText('1 of 4 matching');
  await expect.poll(() => new URL(page.url()).hash).toContain('view=q%3DBeta');

  await page.mouse.move(1360, 960);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `workbench-sort-filter-project--mocked-${theme}.png`),
      fullPage: true,
    });
  }

  await page.evaluate(() => {
    const route = window.location.hash.split('?', 1)[0];
    window.history.replaceState(null, '', window.location.pathname + window.location.search + route);
  });
  await page.reload({ waitUntil: 'commit' });
  await expect(page.getByTestId('workbench-filter-input')).toHaveValue('Beta');
  await expect(page.getByTestId('workbench-sort-key')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId(`workbench-overview-item-${projectName}-beta-route`)).toBeVisible();
});
