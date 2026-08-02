import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

const PROJECTS = ['Agent Studio', 'Runbook', 'Taskboard'];
const WATCH_PATH = '/tmp/agent-studio';
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], escalated: [], completed: [], archive: [],
};

type FeedEntry = {
  ts: string;
  kind: 'alert' | 'decision' | 'action' | 'observation' | 'intervention';
  topic: string;
  summary: string;
  reasoning: string | null;
  project: string;
  watchPath: string;
  jobId: string;
  tokenUsage: {
    model: string;
    thinkingLevel: string;
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
  };
};

function buildEntries(): FeedEntry[] {
  const now = Date.now();
  return Array.from({ length: 500 }, (_, index) => ({
    ts: new Date(now - index * 60_000).toISOString(),
    kind: index < 3 ? 'alert' : index % 5 === 0 ? 'decision' : 'action',
    topic: index < 3 ? 'pipeline-health' : index % 5 === 0 ? 'route/decision' : 'runner/signal',
    summary: index < 3
      ? `Fresh alert ${index + 1} needs operator attention`
      : `Workspace event ${String(index + 1).padStart(3, '0')} completed without intervention`,
    reasoning: index % 5 === 0 ? 'Recorded evidence cleared the configured correctness floor.' : null,
    project: PROJECTS[index % PROJECTS.length],
    watchPath: WATCH_PATH,
    jobId: `AGT-${2440 + index}`,
    tokenUsage: {
      model: 'gpt-5.6-terra',
      thinkingLevel: 'medium',
      inputTokens: 1200 + index,
      outputTokens: 240,
      cacheReadTokens: 4000,
      cacheCreationTokens: 0,
    },
  }));
}

function evidenceDir(testInfo: TestInfo): string {
  const root = process.env['JOB_RESULTS_DIR']?.trim()
    ? resolve(process.env['JOB_RESULTS_DIR'])
    : testInfo.outputDir;
  const dir = join(root, 'feed-main-view');
  mkdirSync(dir, { recursive: true });
  return dir;
}

async function mockStudio(page: Page, currentEntries: () => readonly FeedEntry[]): Promise<void> {
  await page.route('**/update/status', route => route.fulfill({
    json: { phase: 'idle', isRunning: false, behindBy: 0 },
  }));
  await page.route('**/hubs/jobs/negotiate**', route => route.fulfill({
    json: {
      connectionId: 'orchestrator-feed-e2e',
      connectionToken: 'orchestrator-feed-e2e',
      negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    },
  }));
  await page.routeWebSocket('**/hubs/jobs**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });

    if (url.pathname === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/runner/orchestrator-feed') return json({ entries: currentEntries() });
    if (url.pathname === '/api/runner/global/orchestrator-session') {
      return json({
        project: '(global)',
        session: {
          sessionId: 'feed-main-session',
          model: 'gpt-5.6-terra',
          bootedAt: '2026-07-30T07:30:00Z',
          bootPromptPreview: 'Monitor every watched project.',
          bootReplyPreview: 'Monitoring all projects. Three fresh alerts need a closer look.',
          cumulativeInputTokens: 120000,
          cumulativeOutputTokens: 18000,
          cumulativeCacheReadTokens: 420000,
          cumulativeCacheCreationTokens: 12000,
          calls: 24,
          lastUsedAt: '2026-07-30T08:00:00Z',
          lastError: null,
        },
      });
    }
    if (url.pathname === '/api/watch-paths') {
      return json(PROJECTS.map(name => ({ name, path: WATCH_PATH, rootPath: WATCH_PATH })));
    }
    if (url.pathname === '/api/tasks/grouped') return json(EMPTY_GROUPED);
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/tasks') return json([]);
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/runner/pickup-gates') return json({ projects: {} });
    if (url.pathname === '/api/workspaces' || url.pathname === '/api/projects') return json([]);
    if (
      url.pathname === '/api/tags'
      || url.pathname === '/api/clients'
      || url.pathname === '/api/clients/'
    ) return json([]);
    if (url.pathname === '/api/orchestrator/sessions') return json({ sessions: [] });
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname === '/api/epics/completed/count') return json({ count: 0 });
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (/\/api\/cli\/[^/]+\/models$/.test(url.pathname)) return json({ models: [], source: 'feed-e2e' });
    if (url.pathname === '/api/runner/token-summary-aggregate') {
      return json({
        projects: PROJECTS.length,
        orchestratorEntries: currentEntries().length,
        orchestratorLlmCalls: currentEntries().length,
        totalInputTokens: 600000,
        totalOutputTokens: 120000,
        totalCacheReadTokens: 2000000,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 4.2,
        allModelsPriced: true,
        byModel: [],
        byProject: [],
        fetchedAt: '2026-07-30T08:00:00Z',
        disclaimer: 'Captured test data.',
      });
    }
    if (url.pathname === '/api/workspace/tokens/timeline') {
      return json({
        windowStart: '2026-07-29T08:00:00Z',
        windowEnd: '2026-07-30T08:00:00Z',
        windowHours: 24,
        bucketMinutes: 60,
        bucketCount: 24,
        cells: [],
        projects: [],
        fetchedAt: '2026-07-30T08:00:00Z',
        disclaimer: 'Captured test data.',
      });
    }
    if (url.pathname === '/api/workspace/tokens/expensive-jobs') return json({ jobs: [] });
    if (url.pathname === '/api/adhoc-usage') {
      return json({
        calls: 0,
        inputTokens: 0,
        outputTokens: 0,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        estimatedApiCostUsd: 0,
        allModelsPriced: true,
        bySource: [],
        byDay: [],
        byModel: [],
        logPath: '',
        logSizeBytes: 0,
        logModifiedAt: null,
        disclaimer: '',
      });
    }
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (url.pathname === '/api/v1/management/remote-hosts') return json([]);
    return json({});
  });
}

test('Feed main view: route, Activity icon, fresh-alert badge, windowing, live stability, and responsive themes', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  let entries = buildEntries();
  const pageErrors: string[] = [];
  const consoleErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    const text = message.text();
    if (!/favicon/i.test(text)) consoleErrors.push(text);
  });
  await page.addInitScript(() => {
    localStorage.setItem('atp.orchestrator-feed.alerts-seen-at', '2026-01-01T00:00:00Z');
    localStorage.removeItem('atp.studio.tabs.v1');
  });
  await mockStudio(page, () => entries);
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto('/');
  await dismissDevErrorDialog(page);

  const activityIcon = page.getByTestId('studio-ab-activity');
  await expect(activityIcon).toBeVisible();
  await expect(page.getByTestId('studio-ab-badge-activity')).toHaveText('3');

  await activityIcon.click();
  await expect(page).toHaveURL(/#\/feed$/);
  await expect(page.getByTestId('orchestrator-feed')).toHaveAttribute('data-mode', 'embedded');
  await expect(activityIcon).toHaveClass(/studio-ab__btn--active/);
  await expect(page.getByTestId('studio-ab-badge-activity')).toHaveCount(0);

  const renderedEntries = page.getByTestId('orchestrator-feed-entry');
  await expect(renderedEntries).toHaveCount(100);
  await expect(page.getByTestId('orchestrator-feed-load-older')).toContainText('100 older events');
  await expect(page.getByTestId('orchestrator-entry-task').first()).toHaveAttribute('aria-label', /Open task AGT-/);

  const [filters, stream, detail] = await Promise.all([
    page.getByTestId('orchestrator-feed-filters').boundingBox(),
    page.getByTestId('orchestrator-feed-stream').boundingBox(),
    page.getByTestId('orchestrator-feed-detail').boundingBox(),
  ]);
  if (!filters || !stream || !detail) throw new Error('Feed panes did not render');
  expect(stream.x).toBeGreaterThan(filters.x + filters.width - 4);
  expect(detail.x).toBeGreaterThan(stream.x + stream.width - 4);

  await renderedEntries.nth(20).click();
  const selectedSummary = await page.getByTestId('orchestrator-feed-detail').getByRole('heading', { level: 3 }).textContent();
  entries = [{
    ...entries[0],
    ts: new Date(Date.now() + 60_000).toISOString(),
    summary: 'Live alert arrived without replacing the selected detail',
  }, ...entries];
  await page.getByTestId('orchestrator-refresh').click();
  await expect(page.getByTestId('orchestrator-feed-detail').getByRole('heading', { level: 3 }))
    .toHaveText(selectedSummary ?? '');

  const output = evidenceDir(testInfo);
  for (const theme of ['light', 'dark'] as Theme[]) {
    await setTheme(page, theme);
    await page.screenshot({
      path: join(output, `feed-main-wide-${theme}.png`),
      fullPage: false,
    });
  }

  await page.getByTestId('studio-ab-explorer').click();
  await expect(page.getByTestId('studio-sidebar')).toHaveCount(0);
  await page.setViewportSize({ width: 760, height: 1000 });
  for (const theme of ['light', 'dark'] as Theme[]) {
    await setTheme(page, theme);
    await expect(page.getByTestId('orchestrator-feed')).toBeVisible();
    await page.screenshot({
      path: join(output, `feed-main-narrow-${theme}.png`),
      fullPage: false,
    });
  }

  await page.reload();
  await dismissDevErrorDialog(page);
  await expect(page).toHaveURL(/#\/feed$/);
  await expect(page.getByTestId('orchestrator-feed')).toHaveAttribute('data-mode', 'embedded');
  expect(pageErrors).toEqual([]);
  expect(consoleErrors).toEqual([]);
});
