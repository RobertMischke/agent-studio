import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

test.use({ serviceWorkers: 'block' });

const ALPHA = { id: 'PROJ-ENTRY-A', name: 'Entry Alpha', path: '/tmp/entry-alpha' };
const BETA = { id: 'PROJ-ENTRY-B', name: 'Entry Beta', path: '/tmp/entry-beta' };
const TASK = {
  id: 'entry-task',
  key: 'ENT-1',
  taskKey: `${ALPHA.path}::entry-task`,
  title: 'Keep task deep links focused',
  projectName: ALPHA.name,
  watchPath: ALPHA.path,
  state: '2-ready',
};

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};

const GROUPED_WITH_TASK = {
  ...EMPTY_GROUPED,
  ready: [{
    ...TASK,
    displayKey: TASK.key,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: null,
    createdAt: '2026-08-10T12:00:00Z',
    folderPath: `${ALPHA.path}/2-ready/${TASK.id}`,
    lastActivity: '2026-08-10T12:00:00Z',
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
  }],
};

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function evidencePath(testInfo: TestInfo, name: string): string {
  const root = process.env.JOB_RESULTS_DIR?.trim()
    ? resolve(process.env.JOB_RESULTS_DIR)
    : testInfo.outputDir;
  mkdirSync(root, { recursive: true });
  return join(root, name);
}

async function expectNoErrorDialog(page: Page): Promise<void> {
  const dialog = page.getByTestId('error-dialog');
  if (await dialog.count() > 0) {
    const message = await page.getByTestId('error-dialog-message').textContent().catch(() => null);
    throw new Error(`Unexpected frontend error dialog: ${message?.trim() || 'no message'}`);
  }
  const visibleDialog = page.locator('[role="dialog"]:visible, [role="alertdialog"]:visible').first();
  if (await visibleDialog.count() > 0) {
    const identity = await visibleDialog.evaluate(element => ({
      testid: element.getAttribute('data-testid'),
      className: element.className,
      text: element.textContent?.trim(),
    }));
    throw new Error(`Unexpected visible dialog: ${JSON.stringify(identity)}`);
  }
}

function workspaceProject(project: typeof ALPHA) {
  return {
    id: project.id,
    displayName: project.name,
    shortCode: project.id.endsWith('A') ? 'EA' : 'EB',
    workspaceId: 'WS-ENTRY',
    storageLocation: project.path,
    repositoryPath: project.path,
    archived: false,
    urls: [],
  };
}

function projectSnapshot(project: typeof ALPHA) {
  return {
    project: project.name,
    capturedAt: '2026-08-10T12:00:00Z',
    paths: { path: project.path, rootPath: project.path, repositoryPath: project.path },
    settings: {
      autoCommit: true,
      crashRecoveryEnabled: true,
      autoPushStrategy: 'on-completed',
      runnerMode: 'manual',
      orchestratorModel: null,
      orchestratorThinkingLevel: null,
      laneSortStrategies: {},
    },
    runnerStatus: {
      projectName: project.name,
      mode: 'manual',
      activeJobId: null,
      activeExecution: null,
      queuedJobIds: [],
    },
    orchestratorLogTail: [],
    orchestratorSession: null,
    reviewDecisionsPending: [],
    runnerPendingDecisions: [],
    publishTargets: [],
    queueHealth: {
      severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [],
    },
  };
}

function taskDetail() {
  return {
    info: {
      ...TASK,
      displayKey: TASK.key,
      order: 1,
      agent: 'codex',
      createdAt: '2026-08-10T12:00:00Z',
      folderPath: `${ALPHA.path}/2-ready/${TASK.id}`,
      lastActivity: '2026-08-10T12:00:00Z',
      sessionName: null,
      model: null,
      cliType: null,
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: [] },
    },
    promptMarkdown: '# Keep task deep links focused',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

interface RouteOptions {
  chatReplyGate?: Promise<void>;
  sessions?: unknown[];
}

async function installRoutes(
  page: Page,
  requestedChatContexts: string[] = [],
  options: RouteOptions = {},
): Promise<void> {
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = decodeURIComponent(url.pathname);

    if (path === '/api/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (path === '/api/watch-paths') {
      return json(route, [
        {
          name: ALPHA.name,
          path: ALPHA.path,
          rootPath: ALPHA.path,
          repositoryPath: ALPHA.path,
        },
        {
          name: BETA.name,
          path: BETA.path,
          repositoryPath: BETA.path,
        },
      ]);
    }
    if (path === '/api/workspaces') {
      return json(route, [{
        id: 'WS-ENTRY',
        displayName: 'Entry workspace',
        sortOrder: 0,
        isDefault: true,
        projects: [workspaceProject(ALPHA), workspaceProject(BETA)],
      }]);
    }
    if (path === '/api/tasks/grouped') return json(route, GROUPED_WITH_TASK);
    if (path === '/api/tasks/archive') return json(route, { items: [], total: 0, offset: 0, limit: 50, hasMore: false });
    if (path === '/api/tasks/reference-status') return json(route, { items: [] });
    if (path === `/api/tasks/${TASK.key}` || path === `/api/tasks/${TASK.id}`) return json(route, taskDetail());
    if (/\/api\/tasks\/[^/]+\/pipeline$/.test(path)) {
      return json(route, {
        pipeline: {
          id: 'entry-fixture', displayName: 'Entry fixture', version: 1,
          pre: [], core: [], post: [], allSteps: [],
        },
        execution: null,
        cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
        config: {},
      });
    }
    if (/\/api\/tasks\/[^/]+\/plan$/.test(path)) {
      return json(route, {
        hasPlan: false, source: null, snapshotCount: 0, activeItemId: null,
        softEstimateMedian: null, items: [], unassignedSubActions: [],
      });
    }
    if (/\/api\/tasks\/[^/]+\/agent-work-summary$/.test(path)) {
      return json(route, { calls: 0, toolCalls: 0, toolCounts: [], recovered: false });
    }
    if (/\/api\/tasks\/[^/]+\/runs$/.test(path)) return json(route, { runs: [] });
    if (/\/api\/tasks\/[^/]+\/timeline$/.test(path)) return json(route, []);
    if (/\/api\/tasks\/[^/]+\/session-events$/.test(path)) {
      return json(route, { events: [], sessionChain: [] });
    }
    if (path === '/api/tasks' || path === '/api/projects') return json(route, []);
    if (path === '/api/runner/status') return json(route, { projects: {} });
    if (path === '/api/auto-review/status') {
      return json(route, {
        lastTickAt: null, accept: 0, reissue: 0, escalate: 0, aspectsRun: 0,
        pending: 0, currentJob: null, currentProject: null, activeJobs: [],
      });
    }
    if (path === '/api/orchestrator/sessions') return json(route, { sessions: options.sessions ?? [] });
    if (path.startsWith('/api/orchestrator/context/')) {
      const contextKey = path.slice('/api/orchestrator/context/'.length);
      return json(route, {
        contextKey,
        capturedAt: '2026-08-10T12:00:00Z',
        digest: `Project context for ${contextKey}`,
        sources: [],
      });
    }
    if (path.startsWith('/api/runner/') && path.endsWith('/orchestrator-chat')) {
      const contextKey = path.slice('/api/runner/'.length, -'/orchestrator-chat'.length);
      requestedChatContexts.push(contextKey);
      const project = contextKey.startsWith('project:')
        ? contextKey.slice('project:'.length)
        : ALPHA.name;
      if (request.method() === 'POST') {
        await options.chatReplyGate;
        return json(route, {
          contextKey,
          project,
          reply: {
            id: `${contextKey}-reply`, ts: '2026-08-10T12:01:00Z',
            role: 'orchestrator', text: `Finished work for ${project}.`,
          },
        });
      }
      return json(route, {
        contextKey,
        project,
        turns: [{
          id: `${contextKey}-welcome`,
          ts: '2026-08-10T12:00:00Z',
          role: 'orchestrator',
          text: `Project context ready for ${project}.`,
        }],
        executionContext: {
          executionKind: 'local', hostName: 'local', repoPath: `/tmp/${project}`,
          branch: 'develop', headSha: '1234567890abcdef', state: 'ready',
          capturedAt: '2026-08-10T12:00:00Z',
        },
      });
    }
    if (path === '/api/cli/quota') return json(route, { at: '2026-08-10T12:00:00Z', snapshots: [], ttlSeconds: 600 });
    if (/^\/api\/cli\/[^/]+\/models$/.test(path)) return json(route, { models: [], source: 'entry-fixture' });
    if (path === '/api/crash-recovery/pending') return json(route, { pending: [] });
    if (path === '/api/environment' || path === '/api/projects/settings') return json(route, {});
    if (/^\/api\/clients\/[^/]+\/defaults$/.test(path)) return json(route, {});
    if (path === '/api/tags' || path === '/api/clients' || path === '/api/clients/'
      || path === '/api/git/summary' || path === '/api/v1/management/remote-hosts'
      || path.startsWith('/api/bus/')) return json(route, []);

    const project = path.includes(encodeURIComponent(BETA.name)) || path.includes(BETA.name)
      ? BETA : ALPHA;
    if (/\/api\/projects\/[^/]+\/snapshot$/.test(path)) return json(route, projectSnapshot(project));
    if (/\/api\/projects\/[^/]+\/throughput$/.test(path)) {
      return json(route, { project: project.name, capturedAt: '2026-08-10T12:00:00Z', completedLast24h: 0, completedLast7d: 0, recentCompletions: [] });
    }
    if (/\/api\/projects\/[^/]+\/token-usage\/summary$/.test(path)) {
      return json(route, {
        project: project.name, hasData: false, lifetimeTotalTokens: 0,
        lifetimeJobTokens: 0, lifetimeSupportingTokens: 0, lifetimeOrchestratorTokens: 0,
        lifetimeCalls: 0, last24hTotalTokens: 0, last24hJobTokens: 0,
        last24hSupportingTokens: 0, last24hOrchestratorTokens: 0, last24hCalls: 0,
        last7dTotalTokens: 0, last7dJobTokens: 0, last7dSupportingTokens: 0,
        last7dOrchestratorTokens: 0, last7dCalls: 0, firstActivity: null,
        lastActivity: null, fetchedAt: '2026-08-10T12:00:00Z', disclaimer: '',
      });
    }
    if (/\/api\/projects\/[^/]+\/deployment\/summary$/.test(path)) {
      return json(route, { project: project.name, available: false, reason: 'No history.', source: 'fixture', lastDeployment: null, pendingCount: null, pendingCommits: [] });
    }
    if (/\/api\/projects\/[^/]+\/visual-evidence$/.test(path)) {
      return json(route, {
        project: project.name,
        capturedAt: '2026-08-10T12:00:00Z',
        unseenCount: 0,
        items: [],
      });
    }
    if (/\/api\/projects\/[^/]+\/wiki\/pulse$/.test(path)) {
      return json(route, {
        projectName: project.name, baseDir: `${project.path}/docs`, exists: true,
        generatedAtUtc: '2026-08-10T12:00:00Z',
        feed: { available: true, reason: null, items: [] },
        inbox: { available: true, reason: null, count: 0, items: [] },
        drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [], counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
        critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
      });
    }
    if (/\/api\/projects\/[^/]+\/workbenches$/.test(path)) {
      return json(route, {
        projectName: project.name,
        includesHistory: false,
        count: 0,
        items: [],
      });
    }

    return json(route, request.method() === 'GET' ? [] : {});
  });
}

async function seedBrowserState(
  page: Page,
  preference: '1' | '0' | null,
  staleProject?: string,
  panelPosture?: '1' | '0',
): Promise<void> {
  await page.addInitScript(({ savedPreference, persistedProject, savedPanelPosture }) => {
    if (sessionStorage.getItem('project-entry-fixture-seeded')) return;
    sessionStorage.setItem('project-entry-fixture-seeded', '1');
    if (savedPreference === null) {
      localStorage.removeItem('atp.studio.openProjectChatOnEntry.v1');
    } else {
      localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', savedPreference);
    }
    localStorage.setItem('atp.studio.theme', 'light');
    if (savedPanelPosture) {
      sessionStorage.setItem('atp.studio.orchestratorOpen.v1', savedPanelPosture);
    }
    if (persistedProject) {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: persistedProject }],
        activeKey: `board:${persistedProject}`,
      }));
    }
  }, {
    savedPreference: preference,
    persistedProject: staleProject ?? null,
    savedPanelPosture: panelPosture ?? null,
  });
}

test.describe('Orchestrator Chat standard project entry', () => {
  test.setTimeout(120_000);

  test('route and project navigation stay closed instead of auto-opening Chat', async ({ page }, testInfo) => {
    const requestedChatContexts: string[] = [];
    await installRoutes(page, requestedChatContexts);
    await seedBrowserState(page, null, BETA.name);
    await page.setViewportSize({ width: 1440, height: 900 });

    await page.goto(`/#/projects/${ALPHA.id}`, { waitUntil: 'domcontentloaded' });

    await expect(page.getByTestId('project-shell-panel-overview')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet')).toBeHidden();
    expect(requestedChatContexts).not.toContain(`project:${BETA.name}`);
    await expect.poll(() => new URL(page.url()).hash).toBe(`#/projects/${ALPHA.id}`);

    await page.getByTestId('studio-project-picker-trigger')
      .getByText(ALPHA.name, { exact: true })
      .click();
    await page.getByTestId(`studio-project-picker-item-${BETA.name}`).click();
    await expect(page.getByTestId('orch-side-sheet')).toBeHidden();
    await page.mouse.move(1_000, 500);
    await expect(page.getByText('Drag onto a workspace folder to move this project there', { exact: true }))
      .toBeHidden();
    await page.screenshot({
      path: evidencePath(testInfo, 'orchestrator-navigation-stays-closed-light--mocked.png'),
      fullPage: false,
    });
  });

  test('persists open posture and width while the next-message context follows navigation', async ({ page }, testInfo) => {
    const requestedChatContexts: string[] = [];
    const runtimeErrors: string[] = [];
    page.on('pageerror', error => runtimeErrors.push(error.message));
    await installRoutes(page, requestedChatContexts);
    await seedBrowserState(page, '1', ALPHA.name);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/#/projects/${ALPHA.id}`, { waitUntil: 'domcontentloaded' });

    await expect(page.getByTestId('project-shell-panel-overview')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet')).toBeHidden();
    await page.getByTestId('orch-side-sheet-toggle').click();
    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();
    await expect.poll(async () => (await sheet.boundingBox())?.width ?? 0).toBeGreaterThan(630);

    await expect(page.getByTestId('chat-composer-context-project')).toHaveText(ALPHA.name);
    await expect.poll(() => [...requestedChatContexts]).toContain(`project:${ALPHA.name}`);

    await page.getByTestId('orch-context-badge').click();
    await expect(page.getByTestId('orch-context-current')).toContainText(`${ALPHA.name} · Board`);
    await page.getByTestId('orch-context-badge').click();

    const initialWidth = (await sheet.boundingBox())!.width;
    const handle = (await page.getByTestId('orch-side-sheet-resize').boundingBox())!;
    await page.mouse.move(handle.x + handle.width / 2, handle.y + 100);
    await page.mouse.down();
    await page.mouse.move(handle.x + 100, handle.y + 100);
    await page.mouse.up();
    const resizedWidth = await page.evaluate(() =>
      Number(localStorage.getItem('atp.studio.orchestratorWidth')));
    expect(resizedWidth).toBeLessThan(initialWidth - 10);
    await expect.poll(async () => (await sheet.boundingBox())?.width ?? 0)
      .toBeCloseTo(resizedWidth, 0);

    await page.goto(`/#/tasks/${TASK.key}`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('studio-task')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await expect(page.getByTestId('chat-composer-context-surface')).toHaveText('Task');
    await expect.poll(() => [...requestedChatContexts]).toContain(`task:${ALPHA.name}/${TASK.key}`);
    await expect.poll(async () => (await page.getByTestId('orch-side-sheet').boundingBox())?.width ?? 0)
      .toBeCloseTo(resizedWidth, 0);
    expect(runtimeErrors).toEqual([]);
    await expectNoErrorDialog(page);

    await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('studio-task')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await expect.poll(async () => (await page.getByTestId('orch-side-sheet').boundingBox())?.width ?? 0)
      .toBeCloseTo(resizedWidth, 0);
    await expect.poll(() => page.evaluate(() => sessionStorage.getItem('atp.studio.orchestratorOpen.v1'))).toBe('1');
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset['studioTheme'])).toBe('dark');
    await page.waitForTimeout(500);
    expect(runtimeErrors).toEqual([]);
    await expectNoErrorDialog(page);
    await page.screenshot({
      path: evidencePath(testInfo, 'orchestrator-open-task-context-dark--mocked.png'),
      fullPage: false,
    });
  });

  test('keeps the empty-entry opt-out while explicit status-bar entry remains available', async ({ page }, testInfo) => {
    await installRoutes(page);
    await seedBrowserState(page, '1');
    await page.goto('/#/workspace/settings/appearance', { waitUntil: 'domcontentloaded' });

    const preference = page.getByRole('group', { name: 'Open Chat from an empty project entry' });
    await expect(preference).toBeVisible();
    await expect(page.getByTestId('settings-project-chat-entry-open')).toHaveAttribute('aria-pressed', 'true');
    await page.getByTestId('settings-project-chat-entry-closed').click();
    await expect(page.getByTestId('settings-project-chat-entry-closed')).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(() => page.evaluate(() => localStorage.getItem('atp.studio.openProjectChatOnEntry.v1'))).toBe('0');
    await page.screenshot({
      path: evidencePath(testInfo, 'project-entry-preference-light--mocked.png'),
      fullPage: false,
    });

    await page.getByTestId('studio-project-picker-trigger')
      .getByText('All projects', { exact: true })
      .click();
    await page.getByTestId(`studio-project-picker-item-${ALPHA.name}`).click();
    await expect(page.getByTestId(`studio-tab-board:${ALPHA.name}`)).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('orch-side-sheet')).toBeHidden();

    await page.getByTestId('orch-side-sheet-toggle').click();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet-project-select')).toHaveValue(ALPHA.name);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId(`studio-tab-board:${ALPHA.name}`)).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  });

  test('applies S5 only to an explicit project entry from the empty editor', async ({ page }) => {
    await installRoutes(page);
    await seedBrowserState(page, '1');
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [], activeKey: null }));
    });
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    await expect(page.getByTestId('studio-welcome')).toBeVisible();
    await page.getByRole('button', { name: new RegExp(`^${ALPHA.name}`) }).click();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet-project-select')).toHaveValue(ALPHA.name);
  });

  test('does not open the project side sheet over a task deep link', async ({ page }) => {
    await installRoutes(page);
    await seedBrowserState(page, '1', BETA.name);
    await page.goto(`/#/tasks/${TASK.key}`, { waitUntil: 'domcontentloaded' });

    await expect(page.getByTestId('studio-task')).toBeVisible();
    await expect(page.getByTestId('inspector-tab-activity')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet')).toBeHidden();
    await expect.poll(() => new URL(page.url()).hash).toBe(`#/tasks/${TASK.key}`);
  });

  test('marks a pending chat in the shell, side-sheet list, and central Chat History', async ({ page }, testInfo) => {
    let releaseReply!: () => void;
    const replyGate = new Promise<void>(resolve => { releaseReply = resolve; });
    const projectSession = {
      contextKey: `project:${ALPHA.name}`, kind: 'project', projectId: ALPHA.name, taskKey: null,
      updatedAt: '2026-08-10T12:00:00Z', model: 'codex', cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle', queuePosition: 0, summary: 'Review the shell behavior',
    };
    await installRoutes(page, [], { chatReplyGate: replyGate, sessions: [projectSession] });
    await seedBrowserState(page, '1', ALPHA.name, '1');
    await page.goto(`/#/projects/${ALPHA.id}`, { waitUntil: 'domcontentloaded' });

    await page.getByTestId('chat-input').fill('Keep working while I inspect another page.');
    await page.getByTestId('chat-send').click();
    const access = page.getByTestId('orch-side-sheet-toggle');
    await expect(access).toContainText('1 active');
    await expect(access.locator('.statusbar__icon')).toHaveClass(/statusbar__icon--pulse/);

    await page.getByTestId('orch-context-badge').click();
    const switcherRow = page.getByTestId(`chat-switcher-row-project:${ALPHA.name}`);
    await expect(switcherRow).toHaveAttribute('data-runtime-status', 'active');
    await expect(switcherRow).toContainText('working');
    await page.getByTestId('orch-context-badge').click();

    await page.getByTestId('studio-ab-chat-history').click();
    const historyRow = page.locator(`[data-testid="chat-history-row"][data-context-key="project:${ALPHA.name}"]`);
    await expect(historyRow).toHaveAttribute('data-runtime-status', 'active');
    await expect(page.getByTestId('chat-history-active-count')).toContainText('1 chat is working');
    await page.screenshot({
      path: evidencePath(testInfo, 'orchestrator-chat-working-visible-light--mocked.png'),
      fullPage: false,
    });

    releaseReply();
    await expect(access).not.toContainText('active');
    await expect(historyRow).toHaveAttribute('data-runtime-status', 'idle');
    await expect(page.getByTestId('chat-history-active-count')).toHaveCount(0);
  });
});
