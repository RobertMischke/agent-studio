import { test, expect, type Page, type Route } from '@playwright/test';

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/file-source-history';
const JOB_ID = 'file-source-history-test';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function text(route: Route, body: string): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'text/plain',
    body,
  });
}

function detail() {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'File source history fixture',
      state: '2-ready',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/2-ready/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
      createdAt: '2026-06-09T12:00:00Z',
      sessionChain: [],
    },
    promptMarkdown: '# Current prompt\n\nThe current task prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/tasks', (route) => json(route, []));
  await page.route('**/api/tasks/grouped**', (route) => json(route, {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    completed: [],
    archive: [],
  }));
  await page.route('**/api/watch-paths**', (route) => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/clients', (route) => json(route, []));
  await page.route('**/api/agent-rules**', (route) => json(route, []));
  await page.route('**/api/cli/usage**', (route) => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', (route) => json(route, { at: '2026-06-09T00:00:00Z', snapshots: [] }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) => json(route, {
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: [],
      },
    },
  }));

  await page.route(new RegExp(`/api/tasks/${idEsc}/artifacts(\\?|$)`), (route) => json(route, {
    jobId: JOB_ID,
    files: [
      {
        name: 'prompt.md',
        sizeBytes: 46,
        mtime: '2026-06-09T12:10:00Z',
        kind: 'prompt',
      },
    ],
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/prompt\\.md/history(\\?|$)`), (route) => json(route, [
    {
      sha: '2222222',
      at: '2026-06-09T12:10:00Z',
      runIndex: 2,
      verdict: 'pass',
      message: 'update prompt',
      author: 'Agent <agent@example.com>',
      provenance: { source: 'workspace', path: 'prompt.md' },
    },
    {
      sha: '1111111',
      at: '2026-06-09T12:00:00Z',
      runIndex: 1,
      verdict: 'concerns',
      message: 'create prompt',
      author: 'Agent <agent@example.com>',
      provenance: { source: 'workspace', path: 'prompt.md' },
    },
  ]));
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/prompt\\.md/diff(\\?|$)`), (route) =>
    text(route, '@@ -1 +1 @@\n-Old prompt\n+Current prompt\n'));
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/prompt\\.md(\\?|$)`), (route) =>
    text(route, '# Historical prompt\n\nRun two prompt body.'));
  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) => json(route, []));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) => json(route, {
    runCount: 0,
    firstStartedAt: null,
    lastActivityAt: null,
    hasActiveRun: false,
    runs: [],
    promptEntries: [],
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/timeline(\\?|$)`), (route) => json(route, []));
  await page.route(new RegExp(`/api/tasks/${idEsc}/screenshots(\\?|$)`), (route) => json(route, { jobId: JOB_ID, screenshots: [] }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) => json(route, { events: [], sessionChain: [] }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) => json(route, {
    isRepo: true,
    branch: 'main',
    filesChanged: 0,
    totalAdded: 0,
    totalRemoved: 0,
    files: [],
    error: null,
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) => json(route, detail()));
}

test.describe('File source history viewer', () => {
  test('Files tab shows run history, selected version, and run-to-run diff', async ({ page }) => {
    await installRoutes(page);

    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('prompt-tab-description')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('prompt-tab-description').click();

    const promptCard = page.getByTestId('file-card-prompt.md');
    await expect(promptCard).toBeVisible();
    await promptCard.getByTestId('file-source-history-toggle').click();

    await expect(promptCard.getByTestId('file-source-history-timeline')).toContainText('Run #2');
    await expect(promptCard.getByTestId('file-source-history-timeline')).toContainText('pass');
    await expect(promptCard.getByTestId('file-source-version')).toContainText('Historical prompt');
    await expect(promptCard.getByTestId('file-source-diff')).toContainText('-Old prompt');
    await expect(promptCard.getByTestId('file-source-diff')).toContainText('+Current prompt');
  });
});
