import { test, expect, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const JOB_ID = 'tokens-by-model-fixture';
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
      title: 'Tokens by model fixture',
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
      lastUsage: null,
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
    promptMarkdown: '# Tokens by model fixture',
    statusMarkdown: '## Done\n\nFixture status.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

// A run with the core agent on Opus plus the aspect reviewers on Haiku, so the
// per-model breakdown has more than one model per run.
function runRecord(attempt: number, startedAt: string, completedAt: string) {
  return {
    pipelineId: 'standard-task-pipeline',
    pipelineVersion: 1,
    jobId: JOB_ID,
    project: 'agent-taskboard',
    startedAt,
    completedAt,
    attempt,
    previousAttempts: [],
    steps: [
      {
        stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-8', status: 'passed',
        startedAt, completedAt, durationMs: 120000,
        inputTokens: 100000, outputTokens: 10000, cacheReadTokens: 0, cacheCreationTokens: 0,
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported', reason: null, verdict: null, verdictSummary: null,
      },
      {
        stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', status: 'passed',
        startedAt, completedAt, durationMs: 9000,
        inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 0, cacheCreationTokens: 0,
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported', reason: null, verdict: 'pass', verdictSummary: null,
      },
    ],
  };
}

function tokensByModel() {
  // Haiku busiest (2.4M over 3 steps), Opus next (0.22M over 2 steps).
  const haikuRun = {
    model: 'claude-haiku-4-5', modelKnown: true, steps: 1,
    inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 0, cacheCreationTokens: 0,
    totalTokens: 1200000, costUsd: 2.0,
  };
  const opusRun = {
    model: 'claude-opus-4-8', modelKnown: true, steps: 1,
    inputTokens: 100000, outputTokens: 10000, cacheReadTokens: 0, cacheCreationTokens: 0,
    totalTokens: 110000, costUsd: 0.75,
  };
  const run = (attempt: number, current: boolean, startedAt: string) => ({
    attempt, current, startedAt, completedAt: startedAt,
    models: [haikuRun, opusRun],
    totalTokens: 1310000, totalCostUsd: 2.75, anyModelUnknown: false,
  });
  return {
    runs: [
      run(1, false, '2026-06-08T10:00:00Z'),
      run(2, true, '2026-06-09T10:00:00Z'),
    ],
    totalByModel: [
      {
        model: 'claude-haiku-4-5', modelKnown: true, steps: 2,
        inputTokens: 2000000, outputTokens: 400000, cacheReadTokens: 0, cacheCreationTokens: 0,
        totalTokens: 2400000, costUsd: 4.0,
      },
      {
        model: 'claude-opus-4-8', modelKnown: true, steps: 2,
        inputTokens: 200000, outputTokens: 20000, cacheReadTokens: 0, cacheCreationTokens: 0,
        totalTokens: 220000, costUsd: 1.5,
      },
    ],
    totalTokens: 2620000,
    totalCostUsd: 5.5,
    anyModelUnknown: false,
  };
}

function pipeline() {
  const current = runRecord(2, '2026-06-09T10:00:00Z', '2026-06-09T10:02:00Z');
  const previous = runRecord(1, '2026-06-08T10:00:00Z', '2026-06-08T10:02:00Z');
  const coreStep = {
    id: 'core-agent-run', displayName: 'Agent execution', kind: 'core',
    runMode: 'sequential', dependsOn: [], idempotent: false, stub: false,
  };
  const aspectStep = {
    id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect',
    runMode: 'parallel', dependsOn: [], idempotent: true, stub: false,
  };
  return {
    pipeline: {
      id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
      pre: [], core: [coreStep], post: [aspectStep],
      allSteps: [coreStep, aspectStep],
    },
    execution: { ...current, previousAttempts: [previous] },
    cost: {
      steps: [],
      totalInputTokens: 1100000, totalOutputTokens: 210000,
      totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 1310000,
      totalInputCostUsd: 0, totalOutputCostUsd: 0, totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0, totalCostUsd: 2.75, anyModelUnknown: false,
    },
    tokensByModel: tokensByModel(),
    config: {},
  };
}

function runTimeline() {
  return {
    runCount: 2,
    firstStartedAt: '2026-06-08T10:00:00Z',
    lastActivityAt: '2026-06-09T10:02:00Z',
    hasActiveRun: false,
    runs: [],
  };
}

async function installFixtureRoutes(page: Page) {
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json({
    preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    autoReview: [jobDetail().info], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([
    { name: 'agent-taskboard', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ])));
  await page.route('**/api/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/environment**', route => route.fulfill(json({
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/clients', route => route.fulfill(json([])));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ snapshots: [], ttlSeconds: 600 })));

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

test('token usage: collapsible task total sum + per-run rows on one quiet surface', async ({ page }) => {
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page);

  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  const surface = page.getByTestId('overview-pipeline-tokens-by-model');
  await expect(surface).toBeVisible({ timeout: 10000 });

  // TASK TOTAL SUM is collapsed by default: the lifetime total + cost show on
  // the toggle line, but the all-runs-by-model breakdown is hidden.
  const total = page.getByTestId('pipeline-token-usage-total');
  const totalToggle = page.getByTestId('pipeline-token-usage-total-toggle');
  await expect(total).toBeVisible();
  await expect(totalToggle).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId('pipeline-token-usage-grand-total-tokens')).toContainText('2.62M');
  await expect(page.getByTestId('pipeline-token-usage-grand-total-cost')).toContainText('$5.50');
  await expect(page.getByTestId('pipeline-token-usage-total-model')).toHaveCount(0);

  // Each run is its own collapsible row, collapsed by default (no model rows).
  const runs = page.getByTestId('pipeline-token-usage-run');
  await expect(runs).toHaveCount(2);
  await expect(page.getByTestId('pipeline-token-usage-run-model')).toHaveCount(0);

  // Newest-first: the current run renders on top and carries the badge + total.
  const currentRun = runs.first();
  await expect(currentRun).toHaveAttribute('data-current', 'true');
  await expect(currentRun.getByTestId('pipeline-token-usage-run-current')).toBeVisible();
  await expect(currentRun.getByTestId('pipeline-token-usage-run-total')).toContainText('1.31M');

  await surface.screenshot({
    path: RESULTS_DIR ? join(RESULTS_DIR, 'pipeline-tokens-collapsed.png') : 'test-results/pipeline-tokens-collapsed.png',
  });

  // Expand TASK TOTAL SUM -> all-runs-by-model breakdown appears inline.
  await totalToggle.click();
  await expect(totalToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(total.getByTestId('pipeline-token-usage-total-model')).toHaveCount(2);
  await expect(total).toContainText('claude-haiku-4-5');
  await expect(total).toContainText('claude-opus-4-8');

  // Expand the current run -> only that run's per-model rows appear.
  await currentRun.getByTestId('pipeline-token-usage-run-toggle').click();
  await expect(currentRun.getByTestId('pipeline-token-usage-run-model')).toHaveCount(2);
  await expect(page.getByTestId('pipeline-token-usage-run-model')).toHaveCount(2);
  await expect(currentRun).toContainText('claude-haiku-4-5');
  await expect(currentRun).toContainText('claude-opus-4-8');

  await surface.screenshot({
    path: RESULTS_DIR ? join(RESULTS_DIR, 'pipeline-tokens-expanded.png') : 'test-results/pipeline-tokens-expanded.png',
  });
  await saveShot(page, 'pipeline-tokens-by-model-full.png');
});
