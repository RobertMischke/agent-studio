import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

const PROJECT = 'Pricing Drift';
const PROJECT_SLUG = 'pricing-drift';
const WATCH_PATH = '/tmp/pricing-drift';
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], escalated: [], completed: [], archive: [],
};

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/healthz', route => json(route, { status: 'ok' }));
  await page.route('**/update/status', route => json(route, { phase: 'idle', isRunning: false, behindBy: 0 }));
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const path = decodeURIComponent(url.pathname);

    if (path === '/api/auth/status') return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (path === '/api/watch-paths') return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]);
    if (path === '/api/tasks/grouped') return json(route, EMPTY_GROUPED);
    if (path === '/api/tasks/archive') return json(route, { items: [], total: 0 });
    if (path === '/api/tasks' || path === '/api/tags' || path === '/api/clients' || path === '/api/workspaces' || path === '/api/projects') return json(route, []);
    if (path === '/api/environment') return json(route, { isDev: false, devTools: {} });
    if (path === '/api/runner/status' || path === '/api/runner/pickup-gates') return json(route, { projects: {} });
    if (path === `/api/projects/${PROJECT}/snapshot`) {
      return json(route, {
        project: PROJECT,
        capturedAt: '2026-08-09T10:00:00Z',
        paths: { path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
        settings: { autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'always-immediate', runnerMode: 'manual', orchestratorModel: null },
        runnerStatus: null,
        orchestratorLogTail: [],
        orchestratorSession: null,
        reviewDecisionsPending: [],
        runnerPendingDecisions: [],
        publishTargets: [],
        queueHealth: { issueCount: 0 },
      });
    }
    if (path === `/api/runner/${PROJECT}/token-summary`) {
      return json(route, {
        project: PROJECT,
        orchestratorEntries: 2,
        orchestratorLlmCalls: 2,
        totalInputTokens: 24_000_000,
        totalOutputTokens: 881_640,
        totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 0,
        allModelsPriced: false,
        unknownModelCount: 2,
        byModel: [
          { model: 'gpt-future-a', calls: 1, inputTokens: 12_000_000, outputTokens: 440_000, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0, modelPriced: false, unknownModel: true },
          { model: 'gpt-future-b', calls: 1, inputTokens: 12_000_000, outputTokens: 441_640, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0, modelPriced: false, unknownModel: true },
        ],
        disclaimer: 'Estimated from historical list prices.',
      });
    }
    if (path === `/api/projects/${PROJECT}/cli-modes`) return json(route, { resolved: {}, overrides: {}, available: [] });
    if (path === `/api/projects/${PROJECT}/cli-context-modes`) return json(route, { resolved: {}, overrides: {}, available: [] });
    if (path === '/api/cli/claude/models') return json(route, { models: [], source: 'test' });
    if (path === `/api/bus/${PROJECT}/summary`) return json(route, { project: PROJECT, totalMessages: 0, countsByKind: {}, countsByParticipant: {}, countsBySeverity: {} });
    if (path === `/api/bus/${PROJECT}/recent`) return json(route, []);
    if (path === `/api/supervisor/${PROJECT}/observation`) {
      return json(route, { capturedAt: '2026-08-09T10:00:00Z', project: PROJECT, runnerStatus: 'manual', currentJobId: null, currentRunState: null, lastProgressAt: null, quota: null, recentDecisions: [], recentAgentSamples: [], errorCounts: { cliErrorsLastHour: 0, orchestratorErrorsLastHour: 0, runFailuresLastHour: 0 } });
    }
    if (path === `/api/supervisor/${PROJECT}/recent-events`) return json(route, { advisories: [], interventions: [] });
    if (path === `/api/supervisor/${PROJECT}/meta-cycle`) return json(route, { enabled: false, config: null, reports: [] });
    if (path === `/api/analysis/${PROJECT}/reports`) return json(route, { reports: [] });
    if (path === `/api/analysis/${PROJECT}/schedule`) return json(route, {});
    if (path === '/api/cli/quota') return json(route, { snapshots: [], ttlSeconds: 600 });
    if (path === '/api/runner/token-summary-aggregate') return json(route, { projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0, totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [], fetchedAt: '2026-08-09T10:00:00Z', disclaimer: '' });

    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
  });
}

function evidenceDir(testInfo: TestInfo): string {
  const root = process.env.JOB_RESULTS_DIR?.trim()
    ? resolve(process.env.JOB_RESULTS_DIR)
    : testInfo.outputDir;
  const dir = join(root, 'token-pricing-drift');
  mkdirSync(dir, { recursive: true });
  return dir;
}

test('Token Summary makes UnknownModel catalog drift visible in both themes', async ({ page }, testInfo) => {
  await page.addInitScript(project => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'hub', projectName: project, section: 'observability' }],
      activeKey: `hub:${project}`,
    }));
  }, PROJECT);
  await mockBackend(page);
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto(`/#/projects/${PROJECT_SLUG}/observability`, { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);

  const summary = page.getByTestId('token-summary');
  const badge = page.getByTestId('token-summary-pricing-drift');
  await expect(summary).toBeVisible({ timeout: 15_000 });
  await expect(badge).toHaveText(/2 models without price data/);
  await expect(page.getByTestId('token-summary-cost')).toContainText('Unknown');
  await expect(page.getByTestId('token-summary-cost')).not.toContainText('$0.00');

  await badge.hover();
  await expect(page.getByTestId('cac-tooltip')).toContainText('UnknownModel');
  await expect(page.getByTestId('cac-tooltip')).toContainText('gpt-future-a, gpt-future-b');

  const output = evidenceDir(testInfo);
  for (const theme of ['light', 'dark'] as Theme[]) {
    await setTheme(page, theme);
    await summary.screenshot({ path: join(output, `token-pricing-drift--mocked-${theme}.png`) });
  }
});
