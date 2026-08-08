import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Regression evidence for build/test gate outcome semantics.
 *
 * The same fixture can run against stable with
 * GATE_EVIDENCE_CAPTURE=before, where the historical API projection is
 * reproduced, or against the active checkout for the assertions and after
 * captures. Every API call is mocked, so the spec never mutates task data.
 */

const PROJECT = 'AOW static website';
const WATCH_PATH = '/fixtures/aow-static-website';
const STATIC_ID = 'AOW-9';
const SKIPPED_ID = 'AOW-10';
const RESULTS = process.env.JOB_RESULTS_DIR?.trim() || '';
const CAPTURE_PHASE = process.env.GATE_EVIDENCE_CAPTURE === 'before' ? 'before' : 'after';

function evidence(
  kind: 'not-applicable' | 'skipped',
  beforeProjection = false,
) {
  const isNotApplicable = kind === 'not-applicable' && !beforeProjection;
  const commit = kind === 'not-applicable' ? 'd1649ce9' : 'f11eab1e';
  const summary = isNotApplicable
    ? `No build/test commands defined at ${commit}`
    : `Build/test gate skipped at ${commit}`;
  return {
    runId: null,
    runCommit: null,
    runState: null,
    runResult: null,
    matchQuality: 'perfect',
    direction: 'exact',
    distance: 0,
    diffContained: true,
    evidenceState: isNotApplicable ? 'not-applicable' : 'not-proven',
    awaitingEvidence: false,
    summary,
    sources: [{
      kind: 'build-test-gate',
      id: kind === 'not-applicable' ? 'gate-static' : 'gate-interrupted',
      commit,
      result: isNotApplicable ? 'not-applicable' : 'not-proven',
      observedAt: '2026-08-08T10:00:01Z',
      summary,
    }],
  };
}

function task(
  id: string,
  title: string,
  gateEvidence: ReturnType<typeof evidence>,
  order: number,
) {
  return {
    id,
    key: id,
    displayKey: id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state: '5-human-review',
    order,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.6-codex',
    createdAt: '2026-08-08T09:00:00Z',
    lastActivity: '2026-08-08T10:00:01Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/${id}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: gateEvidence.sources[0].commit,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    testEvidence: gateEvidence,
  };
}

function detail(info: ReturnType<typeof task>) {
  return {
    info,
    promptMarkdown: '# Static website verification fixture',
    statusMarkdown: `# Status

- Result: Success
- Tests: Not applicable

## Overview
- Problem: A valid no-command gate outcome looked like a failure.
- Solution: Render not applicable neutrally while preserving real skipped gates as attention states.
`,
    log: [],
    promptHistory: [],
    titleHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: {
      status: 'complete',
      startedAt: '2026-08-08T10:00:02Z',
      finishedAt: '2026-08-08T10:00:03Z',
      errorMessage: null,
    },
  };
}

function pipeline(outcomeClass: 'not-applicable' | 'skipped') {
  const core = {
    id: 'core-agent-run', displayName: 'Agent execution', kind: 'core',
    runMode: 'sequential', dependsOn: [], idempotent: false, stub: false,
  };
  const gate = {
    id: 'post-build-test-gate', displayName: 'Build/test gate', kind: 'tool',
    runMode: 'sequential', dependsOn: ['core-agent-run'], idempotent: true, stub: false,
  };
  return {
    pipeline: {
      id: 'standard-task-pipeline', displayName: 'Standard task pipeline', version: 1,
      pre: [], core: [core], post: [gate], allSteps: [core, gate],
    },
    execution: {
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1,
      jobId: STATIC_ID, project: PROJECT, attempt: 1, previousAttempts: [],
      startedAt: '2026-08-08T09:00:00Z', completedAt: '2026-08-08T10:00:01Z',
      steps: [
        {
          stepId: 'core-agent-run', kind: 'core', status: 'passed',
          durationMs: 3_600_000, startedAt: '2026-08-08T09:00:00Z',
          completedAt: '2026-08-08T10:00:00Z', inputTokens: 8_000,
          outputTokens: 2_000, cacheReadTokens: 0, cacheCreationTokens: 0,
        },
        {
          stepId: 'post-build-test-gate', kind: 'tool', status: 'skipped',
          outcomeClass,
          reason: outcomeClass === 'not-applicable'
            ? 'no verify commands derivable'
            : 'pipeline condition did not match',
          durationMs: 0, startedAt: '2026-08-08T10:00:00Z',
          completedAt: '2026-08-08T10:00:01Z', inputTokens: 0,
          outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
        },
      ],
    },
    cost: {
      steps: [], totalInputTokens: 0, totalOutputTokens: 0,
      totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 0,
      totalInputCostUsd: 0, totalOutputCostUsd: 0,
      totalCacheReadCostUsd: 0, totalCacheCreationCostUsd: 0,
      totalCostUsd: 0, anyModelUnknown: false,
    },
    config: {},
  };
}

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page): Promise<{
  staticTask: ReturnType<typeof task>;
  skippedTask: ReturnType<typeof task>;
}> {
  const staticTask = task(
    STATIC_ID,
    'Static website without build commands',
    evidence('not-applicable', CAPTURE_PHASE === 'before'),
    1,
  );
  const skippedTask = task(
    SKIPPED_ID,
    'Gate interrupted before verification',
    evidence('skipped'),
    2,
  );
  const tasks = [staticTask, skippedTask];
  const project = {
    id: 'PROJ-AOW', displayName: PROJECT, shortCode: 'AOW', workspaceId: 'ws-aow',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
    storageLocation: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
    urls: [],
    archived: false, createdAt: '2026-08-08T08:00:00Z',
  };

  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const pathname = url.pathname;
    if (pathname === '/api/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (pathname === '/api/tasks/grouped') {
      return json(route, {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
        humanReview: tasks, escalated: [], completed: [], archive: [],
      });
    }
    if (pathname === '/api/tasks/archive') return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    if (/^\/api\/(?:tasks|jobs)$/.test(pathname)) return json(route, tasks);
    if (pathname.endsWith('/pipeline')) {
      return json(route, pipeline(pathname.includes(encodeURIComponent(SKIPPED_ID)) ? 'skipped' : 'not-applicable'));
    }
    if (pathname.endsWith('/output')) return json(route, []);
    if (pathname.endsWith('/runs')) return json(route, { runs: [] });
    if (pathname.endsWith('/session-events')) return json(route, { events: [], sessionChain: [] });
    if (pathname.endsWith('/timeline')) return json(route, []);
    if (/^\/api\/(?:tasks|jobs)\//.test(pathname)) {
      const id = decodeURIComponent(pathname.split('/')[3] ?? '');
      const info = tasks.find(item => item.id === id || item.taskKey === id) ?? staticTask;
      return json(route, detail(info));
    }
    if (pathname === '/api/watch-paths') {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]);
    }
    if (pathname === '/api/projects') return json(route, [project]);
    if (pathname === '/api/workspaces') {
      return json(route, [{
        id: 'ws-aow', displayName: 'AOW Workspace', sortOrder: 0, isDefault: true,
        color: null, createdAt: '2026-08-08T08:00:00Z', projects: [project],
      }]);
    }
    if (pathname.endsWith('/build-profile')) {
      return json(route, {
        profile: null, status: null, pickupAllowed: true,
        gateReason: 'no build profile declared', plannedDryRun: null,
        hasVerifyCommands: false, verifyPlanSource: 'none', verifyCommandCount: 0,
      });
    }
    if (pathname.endsWith('/snapshot')) {
      return json(route, {
        project: PROJECT, capturedAt: '2026-08-08T10:00:01Z',
        paths: { path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
        settings: {
          autoCommit: false, crashRecoveryEnabled: true, autoPushStrategy: 'never',
          runnerMode: 'manual', orchestratorModel: null,
        },
        runnerStatus: null, orchestratorLogTail: [], orchestratorSession: null,
        reviewDecisionsPending: [], runnerPendingDecisions: [], publishTargets: [],
        queueHealth: { status: 'healthy', blockers: [], warnings: [] },
      });
    }
    if (pathname.endsWith('/cli-modes')) return json(route, { resolved: {}, overrides: {}, available: [] });
    if (pathname.endsWith('/cli-context-modes')) return json(route, { resolved: {}, overrides: {}, available: [] });
    if (pathname === '/api/projects/settings') return json(route, {});
    if (pathname === '/api/environment') return json(route, { isDev: false, devTools: {} });
    if (pathname === '/api/runner/status') return json(route, { projects: {} });
    if (pathname.includes('/quota-wait')) {
      return json(route, { enabled: false, thresholdMinutes: 60, source: 'workspace' });
    }
    if (pathname === '/api/cli/quota') return json(route, { at: '2026-08-08T10:00:01Z', snapshots: [] });
    if (pathname === '/api/cli/usage') return json(route, { at: '2026-08-08T10:00:01Z', sessions: [] });
    if (pathname.includes('/quota')) return json(route, { defaultCapPct: 95, caps: {} });
    if (pathname === '/api/clients/local-default/defaults') {
      return json(route, { defaultCliType: 'codex', defaultModel: 'gpt-5.6-codex', defaultThinkingLevel: 'high' });
    }
    if (pathname.startsWith('/api/clients')) return json(route, []);
    if (pathname.startsWith('/api/cli/')) return json(route, []);
    if (pathname === '/api/git/inventory') return json(route, { branches: [] });
    return json(route, []);
  });

  return { staticTask, skippedTask };
}

async function bootBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.addInitScript((project) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: project }], activeKey: `board:${project}`,
    }));
    localStorage.setItem(
      'taskboard.panesVisible',
      JSON.stringify({ prompt: true, protocol: true, git: false }),
    );
  }, PROJECT);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('task-card')).toHaveCount(2, { timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await page.evaluate(() => {
    document.querySelector('vite-error-overlay')?.remove();
    document.querySelector('ng-error-overlay')?.remove();
  });
}

function card(page: Page, title: string) {
  return page.getByTestId('task-card').filter({ hasText: title }).first();
}

test('not-applicable stays neutral while a real skipped gate stays conspicuous everywhere', async ({ page }, testInfo) => {
  const fixtures = await installRoutes(page);
  await bootBoard(page);
  await setTheme(page, 'light');

  const staticCard = card(page, fixtures.staticTask.title);
  const skippedCard = card(page, fixtures.skippedTask.title);
  const staticEvidence = staticCard.getByTestId('task-card-test-evidence');
  const skippedEvidence = skippedCard.getByTestId('task-card-test-evidence');

  await expect(staticEvidence).toBeVisible();
  await expect(skippedEvidence).toBeVisible();

  if (CAPTURE_PHASE === 'before') {
    await expect(staticEvidence).toHaveAttribute('data-evidence-state', 'not-proven');
    if (RESULTS) {
      mkdirSync(RESULTS, { recursive: true });
      const screenshotPath = join(RESULTS, 'build-test-gate--before-board--light--mocked.png');
      await page.screenshot({ path: screenshotPath, fullPage: false });
      await testInfo.attach('build-test-gate--before-board--light', {
        path: screenshotPath, contentType: 'image/png',
      });
    }
    return;
  }

  await expect(staticEvidence).toHaveAttribute('data-evidence-state', 'not-applicable');
  await expect(staticEvidence).toContainText('No build/test commands defined');
  await expect(skippedEvidence).toHaveAttribute('data-evidence-state', 'not-proven');
  await expect(skippedEvidence).toContainText('Build/test gate skipped');
  const evidenceSurfaces = await Promise.all([
    staticEvidence.evaluate(element => getComputedStyle(element).backgroundColor),
    skippedEvidence.evaluate(element => getComputedStyle(element).backgroundColor),
  ]);
  expect(evidenceSurfaces[0]).not.toBe(evidenceSurfaces[1]);

  if (RESULTS) {
    mkdirSync(RESULTS, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      const screenshotPath = join(RESULTS, `build-test-gate--after-board--${theme}--mocked.png`);
      await page.screenshot({ path: screenshotPath, fullPage: false });
      await testInfo.attach(`build-test-gate--after-board--${theme}`, {
        path: screenshotPath, contentType: 'image/png',
      });
    }
  }

  await page.goto(`/?job=${encodeURIComponent(STATIC_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissDevErrorDialog(page);
  const pipelineRow = page.locator('[data-step-id="post-build-test-gate"]');
  const phase = page.locator('[data-testid="overview-pipeline-phase"][data-phase="tool"]');
  if (await phase.getAttribute('aria-expanded') === 'false') await phase.click();
  await expect(pipelineRow).toHaveAttribute('data-status', 'not-applicable');
  await expect(pipelineRow).toHaveAttribute('data-gate-outcome', 'not-applicable');

  await page.getByTestId('inspector-tab-protocol').click();
  await expect(page.getByTestId('result-test-evidence')).toHaveAttribute('data-evidence-state', 'not-applicable');
  await setTheme(page, 'light');
  if (RESULTS) {
    const screenshotPath = join(RESULTS, 'build-test-gate--after-task-detail--light--mocked.png');
    await page.screenshot({ path: screenshotPath, fullPage: false });
    await testInfo.attach('build-test-gate--after-task-detail--light', {
      path: screenshotPath, contentType: 'image/png',
    });
  }

  await page.getByTestId('prompt-tab-timeline').click();
  await expect(page.getByTestId('timeline-test-evidence')).toHaveAttribute('data-evidence-state', 'not-applicable');

  const projectSlug = PROJECT.toLowerCase().replace(/[^a-z0-9]+/g, '-');
  await page.goto(`/#/projects/${projectSlug}/settings`);
  const settingsHint = page.getByTestId('project-settings-no-verify-commands');
  await expect(settingsHint).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await expect(settingsHint).toContainText('No BuildProfile exists');
  await expect(settingsHint.getByRole('link', { name: 'BuildProfile convention' }))
    .toHaveAttribute('href', /contributor-setup\.md#/);
  if (RESULTS) {
    const screenshotPath = join(RESULTS, 'build-test-gate--after-project-settings--light--mocked.png');
    await settingsHint.scrollIntoViewIfNeeded();
    await page.screenshot({ path: screenshotPath, fullPage: false });
    await testInfo.attach('build-test-gate--after-project-settings--light', {
      path: screenshotPath, contentType: 'image/png',
    });
  }
});
