import { test, expect, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

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

function pipeline() {
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
        model: 'claude-opus-4-8',
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
        model: 'claude-opus-4-8',
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
        modelKnown: true,
        inputTokens: 2500,
        outputTokens: 195600,
        cacheReadTokens: 18500000,
        cacheCreationTokens: 1000000,
        totalTokens: 19698100,
        inputCostUsd: 0.0125,
        outputCostUsd: 4.89,
        cacheReadCostUsd: 9.25,
        cacheCreationCostUsd: 6.25,
        costUsd: 20.4025,
      }],
      totalInputTokens: 2500,
      totalOutputTokens: 195600,
      totalCacheReadTokens: 18500000,
      totalCacheCreationTokens: 1000000,
      totalTokens: 19698100,
      totalInputCostUsd: 0.0125,
      totalOutputCostUsd: 4.89,
      totalCacheReadCostUsd: 9.25,
      totalCacheCreationCostUsd: 6.25,
      totalCostUsd: 20.4025,
      anyModelUnknown: false,
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

async function installFixtureRoutes(page: Page) {
  await page.route('**/api/**', route => route.fulfill(json([])));
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
  await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route => route.fulfill(json(pipeline())));
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
  await expect(page.getByTestId('overview-pipeline-step-name')).toContainText('Agent execution');
  await expect(page.getByTestId('overview-pipeline-agent-runs')).toContainText('8 runs');
  await expect(page.getByTestId('overview-pipeline-step-tokens')).toContainText('19.70M');
  await expect(page.getByTestId('overview-pipeline-step-cost')).toContainText('$20.40');
  await expect(page.getByTestId('overview-pipeline-total')).toContainText('SUM');
  await expect(page.getByTestId('overview-pipeline-total-tokens')).toContainText('19.70M');

  await pipelineBlock.screenshot({ path: RESULTS_DIR ? join(RESULTS_DIR, 'pipeline-core-token-usage.png') : 'test-results/pipeline-core-token-usage.png' });

  await page.getByTestId('overview-pipeline-step-tokens').hover();
  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toContainText('Source: AGENT (CLI FOOTER) / reported');
  await expect(tooltip).toContainText('Input: 2.5k');
  await expect(tooltip).toContainText('Output: 195.6k');
  await expect(tooltip).toContainText('Cache read: 18.50M');
  await expect(tooltip).toContainText('Cache creation: 1.00M');
  await expect(tooltip).toContainText('Total API price estimate: $20.40');
  await expect(tooltip).toContainText('API price estimate only');
  await expect(tooltip).toContainText('Actual CLI billing uses the subscription or plan, not these API rates');
  await saveShot(page, 'pipeline-core-token-tooltip.png');
});
