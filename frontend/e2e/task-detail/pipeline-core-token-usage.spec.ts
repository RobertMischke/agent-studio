import { test, expect, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setTheme, type Theme } from '../helpers/theme';

const JOB_ID = 'core-token-usage-fixture';
const WATCH_PATH = 'C:/fixtures/agent-taskboard';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

function json(body: unknown) {
  return {
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  };
}

function jobDetail() {
  return {
    info: {
      id: JOB_ID,
      jobKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'CORE CLI-footer token usage fixture',
      state: '4-auto-review',
      agent: 'codex',
      cliType: 'codex',
      model: 'claude-opus-4-8',
      thinkingLevel: null,
      watchPath: WATCH_PATH,
      projectName: 'agent-taskboard',
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/4-auto-review/${JOB_ID}`,
      sessionName: 'fixture-session',
      tokenSummary: null,
      lastUsage: {
        at: '2026-06-06T20:00:00Z',
        tokens: '19.7M',
        changes: null,
        requests: '8',
      },
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      codeActivityDetected: false,
      useOwnSession: null,
      kind: 'task',
      mode: 'coding',
      allowWebAccess: false,
      summaryState: null,
      outcomeIssue: null,
      orchestratorVerdict: null,
      ownerClientId: 'local-default',
    },
    promptMarkdown: '# Token usage fixture',
    statusMarkdown: '## Done\n\nFixture status.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

function pipeline(pricingState: 'priced' | 'unpriced' = 'priced') {
  const coreStep = {
    id: 'core-agent-run',
    displayName: 'Agent execution',
    kind: 'core',
    runMode: 'sequential',
    dependsOn: [],
    idempotent: false,
    stub: false,
  };
  const startedAt = '2026-06-06T20:00:00Z';
  const completedAt = '2026-06-06T20:02:05Z';
  const unpriced = pricingState === 'unpriced';
  const model = unpriced ? 'future-experimental' : 'claude-opus-4-8';
  const totalCostUsd = unpriced ? 0 : 20.4025;
  const modelUsage = {
    model,
    modelKnown: !unpriced,
    steps: 1,
    inputTokens: 2500,
    outputTokens: 195600,
    cacheReadTokens: 18500000,
    cacheCreationTokens: 1000000,
    totalTokens: 19698100,
    costUsd: totalCostUsd,
  };
  return {
    pipeline: {
      id: 'standard-task-pipeline',
      displayName: 'Standard',
      version: 1,
      pre: [],
      core: [coreStep],
      post: [],
      allSteps: [coreStep],
    },
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: 'agent-taskboard',
      startedAt,
      completedAt,
      attempt: 8,
      previousAttempts: [],
      steps: [{
        stepId: 'core-agent-run',
        kind: 'core',
        model,
        status: 'passed',
        startedAt,
        completedAt,
        durationMs: 125000,
        inputTokens: 2500,
        outputTokens: 195600,
        cacheReadTokens: 18500000,
        cacheCreationTokens: 1000000,
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
        reason: null,
        verdict: null,
        verdictSummary: null,
      }],
    },
    cost: {
      steps: [{
        stepId: 'core-agent-run',
        kind: 'core',
        model,
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
        modelKnown: !unpriced,
        inputTokens: 2500,
        outputTokens: 195600,
        cacheReadTokens: 18500000,
        cacheCreationTokens: 1000000,
        totalTokens: 19698100,
        inputCostUsd: unpriced ? 0 : 0.0125,
        outputCostUsd: unpriced ? 0 : 4.89,
        cacheReadCostUsd: unpriced ? 0 : 9.25,
        cacheCreationCostUsd: unpriced ? 0 : 6.25,
        costUsd: totalCostUsd,
      }],
      totalInputTokens: 2500,
      totalOutputTokens: 195600,
      totalCacheReadTokens: 18500000,
      totalCacheCreationTokens: 1000000,
      totalTokens: 19698100,
      totalInputCostUsd: unpriced ? 0 : 0.0125,
      totalOutputCostUsd: unpriced ? 0 : 4.89,
      totalCacheReadCostUsd: unpriced ? 0 : 9.25,
      totalCacheCreationCostUsd: unpriced ? 0 : 6.25,
      totalCostUsd,
      anyModelUnknown: unpriced,
    },
    tokensByModel: {
      runs: [{
        attempt: 8,
        current: true,
        startedAt,
        completedAt,
        models: [modelUsage],
        totalTokens: modelUsage.totalTokens,
        totalCostUsd,
        anyModelUnknown: unpriced,
      }],
      totalByModel: [modelUsage],
      totalTokens: modelUsage.totalTokens,
      totalCostUsd,
      anyModelUnknown: unpriced,
    },
    config: {},
  };
}

function runTimeline() {
  return {
    runCount: 8,
    firstStartedAt: '2026-06-06T19:00:00Z',
    lastActivityAt: '2026-06-06T20:02:05Z',
    hasActiveRun: false,
    runs: Array.from({ length: 8 }, (_, i) => ({
      index: i + 1,
      intent: i === 0 ? 'start' : 'continue',
      startedAt: `2026-06-06T19:${String(i).padStart(2, '0')}:00Z`,
      endedAt: `2026-06-06T19:${String(i).padStart(2, '0')}:30Z`,
      status: 'completed',
      cli: 'codex',
      exitCode: 0,
      durationSeconds: 30,
      inputSessionId: null,
      capturedSessionId: `session-${i + 1}`,
      resumed: i > 0,
      reason: null,
      userFollowup: null,
      lineStart: null,
      lineEnd: null,
      headShaBefore: null,
      headShaAfter: null,
      contextRef: null,
    })),
  };
}

async function installFixtureRoutes(page: Page, pricingState: 'priced' | 'unpriced' = 'priced') {
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  })));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json({
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    autoReview: [jobDetail().info],
    humanReview: [],
    completed: [],
    archive: [],
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([
    { name: 'agent-taskboard', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ])));
  await page.route('**/api/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/environment**', route => route.fulfill(json({
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/clients', route => route.fulfill(json([])));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({
    snapshots: [],
    ttlSeconds: 600,
  })));

  const id = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route => route.fulfill(json(pipeline(pricingState))));
  await page.route(new RegExp(`/api/tasks/${id}/runs(\\?|$)`), route => route.fulfill(json(runTimeline())));
  await page.route(new RegExp(`/api/tasks/${id}/output(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}/session-events(\\?|$)`), route => route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(new RegExp(`/api/tasks/${id}/agent-work(\\?|$)`), route => route.fulfill(json(null)));
  await page.route(new RegExp(`/api/tasks/${id}/timeline(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}/claude-session(\\?|$)`), route => route.fulfill(json(null)));
  await page.route(new RegExp(`/api/tasks/${id}/screenshots(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}(\\?|$)`), route => route.fulfill(json(jobDetail())));
}

async function saveShot(page: Page, name: string) {
  const buf = await page.screenshot({ fullPage: false });
  if (!RESULTS_DIR) return;
  await mkdir(RESULTS_DIR, { recursive: true });
  await writeFile(join(RESULTS_DIR, name), buf);
}

test('task detail pipeline shows CORE CLI-footer usage, SUM footer, and API-price disclaimer', async ({ page }) => {
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page);

  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  const pipelineBlock = page.getByTestId('overview-pipeline');
  await expect(pipelineBlock).toBeVisible({ timeout: 10000 });
  await page.getByTestId('overview-pipeline-group').first().click();
  await expect(page.getByTestId('overview-pipeline-step-name')).toContainText('Agent execution');
  await expect(page.getByTestId('overview-pipeline-agent-runs')).toContainText('8 runs');
  await expect(page.getByTestId('overview-pipeline-step-tokens')).toContainText('19.7m');
  await expect(page.getByTestId('overview-pipeline-step-cost')).toContainText('$20.40');
  await expect(page.getByTestId('overview-pipeline-total')).toContainText('SUM');
  await expect(page.getByTestId('overview-pipeline-total-tokens')).toContainText('19.7m');

  await pipelineBlock.screenshot({ path: RESULTS_DIR ? join(RESULTS_DIR, 'pipeline-core-token-usage.png') : 'test-results/pipeline-core-token-usage.png' });

  await page.getByTestId('overview-pipeline-step-tokens').hover();
  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toContainText('Source: AGENT (CLI FOOTER) / reported');
  await expect(tooltip).toContainText('Input: 2.5k');
  await expect(tooltip).toContainText('Output: 195.6k');
  await expect(tooltip).toContainText('Cache read: 18.5m');
  await expect(tooltip).toContainText('Cache creation: 1m');
  await expect(tooltip).toContainText('Estimated cost: $20.40');
  await expect(tooltip).toContainText('historical list prices');
  await expect(tooltip).toContainText('discounts and provider-side caching adjustments are not considered');
  await saveShot(page, 'pipeline-core-token-tooltip.png');
});

test('TASK TOTAL SUM renders Unknown for entirely unpriced usage in both themes', async ({ page }) => {
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page, 'unpriced');
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  const totalCost = page.getByTestId('overview-pipeline-total-cost');
  await expect(totalCost).toBeVisible({ timeout: 10_000 });
  await expect(totalCost).toHaveText('Unknown');
  await expect(totalCost).not.toContainText('$0.00');

  for (const theme of ['light', 'dark'] as Theme[]) {
    await setTheme(page, theme);
    if (RESULTS_DIR) await mkdir(RESULTS_DIR, { recursive: true });
    await page.getByTestId('overview-pipeline-total').screenshot({
      path: RESULTS_DIR
        ? join(RESULTS_DIR, `task-total-unpriced--mocked-${theme}.png`)
        : `test-results/task-total-unpriced--mocked-${theme}.png`,
    });
  }
});
