import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';

const PROJECT = 'post-processing-identity';
const WATCH_PATH = 'C:/fixtures/post-processing-identity';
const SHOTS = process.env.JOB_RESULTS_DIR
  ? `${process.env.JOB_RESULTS_DIR}/post-processing-lane`
  : 'screenshots/post-processing-lane';

interface JobInfoStub {
  id: string;
  taskKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: null;
  model: string | null;
  cliType: string | null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  commits: unknown[];
  ownerClientId: string;
  tokenSummary: {
    calls: number;
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
    totalTokens: number;
    lastModel: string | null;
    lastUpdate: string | null;
    entries: Array<{
      ts: string;
      model: string | null;
      participantId?: string | null;
      inputTokens: number;
      outputTokens: number;
      cacheReadTokens: number;
      cacheCreationTokens: number;
    }>;
  } | null;
  phase: string | null;
  phaseEnteredAt?: string | null;
  tags: string[];
  taskType: string;
}

function jobInfo(over: Partial<JobInfoStub> = {}): JobInfoStub {
  const id = over.id ?? 'post-processing-codex-main';
  const state = over.state ?? '4-auto-review';
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: over.title ?? 'Codex task with Claude post-processing',
    state,
    order: over.order ?? 1,
    agent: over.agent ?? 'codex',
    createdAt: '2026-06-09T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/tasks/000/${id}`,
    lastActivity: '2026-06-09T08:12:00Z',
    sessionName: null,
    model: over.model ?? 'GPT-5 Codex',
    cliType: over.cliType ?? 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    phase: over.phase ?? 'post-processing-running',
    phaseEnteredAt: over.phaseEnteredAt ?? null,
    tags: [],
    taskType: 'feature',
    tokenSummary: over.tokenSummary ?? {
      calls: 2,
      inputTokens: 72_000,
      outputTokens: 9_000,
      cacheReadTokens: 80_000,
      cacheCreationTokens: 4_000,
      totalTokens: 165_000,
      lastModel: 'Claude Haiku 4.5',
      lastUpdate: '2026-06-09T08:12:00Z',
      entries: [
        {
          ts: '2026-06-09T08:00:00Z',
          model: 'GPT-5 Codex',
          participantId: 'agent:codex',
          inputTokens: 52_000,
          outputTokens: 8_000,
          cacheReadTokens: 60_000,
          cacheCreationTokens: 3_000,
        },
        {
          ts: '2026-06-09T08:12:00Z',
          model: 'Claude Haiku 4.5',
          participantId: 'supporting-agent:post-processing',
          inputTokens: 20_000,
          outputTokens: 1_000,
          cacheReadTokens: 20_000,
          cacheCreationTokens: 1_000,
        },
      ],
    },
    ...over,
  };
}

function grouped(jobs: JobInfoStub[]) {
  const autoReview = jobs.filter((j) => j.state === '4-auto-review');
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: jobs.filter((j) => j.state === '2-ready'),
    progress: jobs.filter((j) => j.state === '3-progress'),
    failedPickup: [],
    codeNotComplete: [],
    autoReview,
    review: autoReview,
    humanReview: jobs.filter((j) => j.state === '5-human-review'),
    escalated: [],
    completed: [],
    archive: [],
  };
}

async function installRoutes(page: Page, jobs: JobInfoStub[]): Promise<void> {
  const groupedBody = grouped(jobs);
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/tasks/grouped' || p === '/api/tasks/grouped') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(groupedBody) });
    }
    if (p === '/api/tasks' || p === '/api/tasks/' || p === '/api/tasks' || p === '/api/tasks/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p === '/api/auto-review/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          lastTickAt: '2026-06-09T08:12:00Z',
          accept: 0,
          reissue: 0,
          escalate: 0,
          aspectsRun: 1,
          pending: 1,
          currentJob: 'post-processing-codex-main',
          currentProject: PROJECT,
        }),
      });
    }
    if (p === '/api/watch-paths') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
      });
    }
    if (p === '/api/workspaces') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          id: 'default',
          displayName: 'Workspaces',
          sortOrder: 0,
          isDefault: true,
          color: null,
          createdAt: '2026-06-09T08:00:00Z',
          projects: [{
            id: PROJECT,
            displayName: PROJECT,
            shortCode: 'PP',
            workspaceId: 'default',
            color: null,
            cliDefault: null,
            modelDefault: null,
            sortOrder: 0,
            storageLocation: WATCH_PATH,
            archived: false,
            createdAt: '2026-06-09T08:00:00Z',
          }],
        }]),
      });
    }
    if (p.startsWith('/api/clients')) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          id: 'local-default',
          displayName: 'Local Default',
          emoji: '🤖',
          colour: '#64748b',
          kind: 'agent-instance',
          registeredAt: '2026-01-01T00:00:00Z',
          lastSeenAt: null,
          tokenBudgetMonthly: null,
          notes: null,
          defaultCliType: null,
          defaultModel: null,
        }]),
      });
    }
    if (p === '/api/environment') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
      });
    }
    if (p === '/api/projects/settings') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
    }
    if (p === '/api/dev-tools/flags') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }),
      });
    }
    if (p === '/api/orchestrator/global') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
    }
    if (p === '/api/cli/quota') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ at: '2026-06-09T08:12:00Z', ttlSeconds: 600, snapshots: [] }),
      });
    }
    if (p === '/api/cli/usage') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ at: '2026-06-09T08:12:00Z', sections: [] }),
      });
    }
    if (p.startsWith('/api/runner')) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }),
      });
    }
    if (p === '/api/tags' || p === '/api/git/summary' || p === '/api/git/projects') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

async function dismissRuntimeErrorOverlay(page: Page): Promise<void> {
  const error = page.getByText('Unexpected application error', { exact: true });
  if (await error.isVisible()) {
    await page.keyboard.press('Escape');
    await expect(error).toBeHidden();
  }
}

test.describe('Post Processing lane identity', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('shows Codex as the coding agent and Claude as supporting post-processing evidence', async ({ page }) => {
    const job = jobInfo();
    await seedBoardTab(page);
    await installRoutes(page, [job]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const lane = page.getByTestId('lane-4-auto-review');
    await expect(lane).toBeVisible({ timeout: 10_000 });
    await expect(lane.getByRole('heading', { name: 'Post Processing' })).toBeVisible();

    const card = page.locator('[data-testid="task-card"]', { hasText: job.title });
    await expect(card).toBeVisible({ timeout: 10_000 });
    await expect(card.getByTestId('task-card-state')).toHaveCount(0);
    await expect(card.getByTestId('task-card-phase')).toContainText('Post processing');
    await expect(card.getByTestId('task-card-effective-model')).toContainText('Codex');

    const bubble = card.getByTestId('task-card-token-bubble');
    await expect(bubble).toBeVisible();
    await bubble.focus();

    const popover = page.getByTestId('task-card-token-popover');
    await expect(popover).toBeVisible();
    await expect(popover).toContainText('GPT-5 Codex');
    await expect(popover).toContainText('Claude Haiku 4.5');

    await dismissRuntimeErrorOverlay(page);
    mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({ path: `${SHOTS}/post-processing-codex-claude--mocked.png`, fullPage: false });
  });

  test('shows a timed loop-waiting phase without claiming a runner slot', async ({ page }) => {
    const job = jobInfo({
      id: 'loop-waiting-card',
      title: 'Waiting for orchestrator loop continuation',
      state: '3-progress',
      phase: 'loop-waiting',
      phaseEnteredAt: new Date(Date.now() - 42_000).toISOString(),
    });
    await seedBoardTab(page);
    await installRoutes(page, [job]);

    await page.goto('/');
    const card = page.locator('[data-testid="task-card"]', { hasText: job.title });
    await expect(card).toBeVisible({ timeout: 10_000 });
    await expect(card.getByTestId('task-card-phase'))
      .toContainText(/Waiting for loop continuation 0:4[2-9]/);

    await dismissRuntimeErrorOverlay(page);
    mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({
      path: `${SHOTS}/loop-waiting-phase--mocked.png`,
      fullPage: false,
    });
  });
});
