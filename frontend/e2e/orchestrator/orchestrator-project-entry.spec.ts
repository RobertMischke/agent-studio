import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

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

async function installRoutes(page: Page, requestedChatContexts: string[] = []): Promise<void> {
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
    if (path === '/api/tasks/grouped') return json(route, EMPTY_GROUPED);
    if (path === '/api/tasks/archive') return json(route, { items: [], total: 0, offset: 0, limit: 50, hasMore: false });
    if (path === `/api/tasks/${TASK.key}` || path === `/api/tasks/${TASK.id}`) return json(route, taskDetail());
    if (path === '/api/tasks' || path === '/api/projects') return json(route, []);
    if (path === '/api/runner/status') return json(route, { projects: {} });
    if (path === '/api/auto-review/status') {
      return json(route, {
        lastTickAt: null, accept: 0, reissue: 0, escalate: 0, aspectsRun: 0,
        pending: 0, currentJob: null, currentProject: null, activeJobs: [],
      });
    }
    if (path === '/api/orchestrator/sessions') return json(route, { sessions: [] });
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

    return json(route, request.method() === 'GET' ? {} : {});
  });
}

async function seedBrowserState(
  page: Page,
  preference: '1' | '0' | null,
  staleProject?: string,
): Promise<void> {
  await page.addInitScript(({ savedPreference, persistedProject }) => {
    if (sessionStorage.getItem('project-entry-fixture-seeded')) return;
    sessionStorage.setItem('project-entry-fixture-seeded', '1');
    if (savedPreference === null) {
      localStorage.removeItem('atp.studio.openProjectChatOnEntry.v1');
    } else {
      localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', savedPreference);
    }
    localStorage.setItem('atp.studio.theme', 'light');
    if (persistedProject) {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: persistedProject }],
        activeKey: `board:${persistedProject}`,
      }));
    }
  }, { savedPreference: preference, persistedProject: staleProject ?? null });
}

test.describe('Orchestrator Chat standard project entry', () => {
  test.setTimeout(120_000);

  test('opens the canonical project route in its project context without a foreign flash or focus steal', async ({ page }, testInfo) => {
    const requestedChatContexts: string[] = [];
    await installRoutes(page, requestedChatContexts);
    await seedBrowserState(page, null, BETA.name);
    await page.setViewportSize({ width: 1440, height: 900 });

    await page.goto(`/#/projects/${ALPHA.id}`, { waitUntil: 'domcontentloaded' });

    await expect(page.getByTestId('project-shell-panel-overview')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet-project-select')).toHaveValue(ALPHA.name);
    await expect(page.getByTestId('chat-composer-context-project')).toHaveText(ALPHA.name);
    await expect(page.getByTestId('chat-input')).not.toBeFocused();
    await expect.poll(() => [...requestedChatContexts]).toContain(`project:${ALPHA.name}`);
    expect(requestedChatContexts).not.toContain(`project:${BETA.name}`);
    await expect.poll(() => new URL(page.url()).hash).toBe(`#/projects/${ALPHA.id}`);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `project-entry-wide-${theme}--mocked.png`),
        fullPage: false,
      });
    }

    await page.getByTestId('studio-project-picker-trigger')
      .getByText(ALPHA.name, { exact: true })
      .click();
    await page.getByTestId(`studio-project-picker-item-${BETA.name}`).click();
    await expect(page.getByTestId('orch-side-sheet-project-select')).toHaveValue(BETA.name);
    await expect(page.getByTestId('chat-composer-context-project')).toHaveText(BETA.name);
    await expect.poll(() => [...requestedChatContexts]).toContain(`project:${BETA.name}`);

    await page.setViewportSize({ width: 430, height: 844 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
      await page.screenshot({
        path: evidencePath(testInfo, `project-entry-narrow-${theme}--mocked.png`),
        fullPage: false,
      });
    }
  });

  test('persists the visible opt-out while Board and status-bar entry remain available', async ({ page }, testInfo) => {
    await installRoutes(page);
    await seedBrowserState(page, '1');
    await page.goto('/#/workspace/settings/appearance', { waitUntil: 'domcontentloaded' });

    const preference = page.getByRole('group', { name: 'Open project Chat on project entry' });
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
    await expect(page.getByTestId('orch-side-sheet')).toBeHidden();
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
});
