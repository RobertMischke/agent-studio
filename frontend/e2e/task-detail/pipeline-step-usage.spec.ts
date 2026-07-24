import { type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

const JOB_ID = 'pipeline-step-usage-fixture';
const WATCH_PATH = 'C:/fixtures/agent-taskboard';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

test.use({ serviceWorkers: 'block' });

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
      title: 'Pipeline step usage fixture',
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
    promptMarkdown: '# Pipeline step usage fixture',
    statusMarkdown: '## Done\n\nFixture status.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

// A run with the core agent on Opus plus the aspect reviewer on Haiku, so each
// pipeline step can surface its own usage where the step is rendered.
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

function costStep(stepId: string, kind: string, model: string, totalTokens: number, costUsd: number) {
  return {
    stepId,
    kind,
    model,
    tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
    modelKnown: true,
    inputTokens: Math.round(totalTokens * 0.8),
    outputTokens: Math.round(totalTokens * 0.2),
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    totalTokens,
    inputCostUsd: costUsd * 0.6,
    outputCostUsd: costUsd * 0.4,
    cacheReadCostUsd: 0,
    cacheCreationCostUsd: 0,
    costUsd,
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
  const wikiSteps = [
    { id: 'post-wiki-maintenance', displayName: 'Wiki maintenance', kind: 'tool', runMode: 'sequential', dependsOn: ['core-agent-run'], idempotent: true, stub: false },
    { id: 'post-wiki-learnings', displayName: 'Wiki learnings', kind: 'tool', runMode: 'sequential', dependsOn: ['aspect-code-quality'], idempotent: true, stub: false },
    { id: 'post-agents-wiki-sync', displayName: 'Agent skills / AGENTS wiki sync', kind: 'tool', runMode: 'sequential', dependsOn: ['core-agent-run'], idempotent: true, stub: false },
  ];
  return {
    pipeline: {
      id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
      pre: [], core: [coreStep], post: [aspectStep, ...wikiSteps],
      allSteps: [coreStep, aspectStep, ...wikiSteps],
    },
    execution: { ...current, previousAttempts: [previous] },
    cost: {
      steps: [
        costStep('core-agent-run', 'core', 'claude-opus-4-8', 110000, 0.75),
        costStep('aspect-code-quality', 'aspect', 'claude-haiku-4-5', 1200000, 2.0),
      ],
      totalInputTokens: 1100000, totalOutputTokens: 210000,
      totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 1310000,
      totalInputCostUsd: 0, totalOutputCostUsd: 0, totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0, totalCostUsd: 2.75, anyModelUnknown: false,
    },
    config: {
      'core-agent-run': {
        enabled: true, canDisable: false, enabledSource: 'catalogue',
        activation: { state: 'active', source: 'global', reason: 'Enabled by the global catalogue default.' },
      },
      'aspect-code-quality': {
        enabled: true, canDisable: true, enabledSource: 'catalogue',
        activation: { state: 'active', source: 'global', reason: 'Enabled by the global catalogue default.' },
      },
      'post-wiki-maintenance': {
        enabled: true, canDisable: true, enabledSource: 'catalogue',
        activation: {
          state: 'active', source: 'condition',
          reason: 'Enabled by the global catalogue default; condition "task has tag \'wiki\'" matched this run.',
        },
      },
      'post-wiki-learnings': {
        enabled: false, canDisable: true, enabledSource: 'project',
        activation: { state: 'inactive', source: 'project', reason: 'Disabled by the project override.' },
      },
      'post-agents-wiki-sync': {
        enabled: true, canDisable: true, enabledSource: 'catalogue',
        activation: {
          state: 'skipped', source: 'condition',
          reason: 'Condition "an aspect failed" did not match this run.',
        },
      },
    },
    onDemand: {
      plannedStepIds: ['post-wiki-maintenance'],
      attempts: [
        {
          stepId: 'post-wiki-maintenance', attempt: 1, status: 'Warn',
          summary: 'created the first maintenance note',
          startedAt: '2026-07-11T00:15:00Z', finishedAt: '2026-07-11T00:15:01Z', durationMs: 700,
          artifactRef: 'results/post-steps/post-wiki-maintenance-attempt-001.md',
        },
        {
          stepId: 'post-wiki-maintenance', attempt: 2, status: 'Ok',
          summary: 'updated existing problem entry',
          startedAt: '2026-07-12T00:15:00Z', finishedAt: '2026-07-12T00:15:01Z', durationMs: 850,
          artifactRef: 'results/post-steps/post-wiki-maintenance-attempt-002.md',
        },
      ],
    },
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
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
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
  await page.route(/\/api\/token-pricing\/calculate/, route => route.fulfill(json({
    provider: 'CodingAgentRunner (CAR)',
    items: [{
      model: 'claude-opus-4-8', label: 'Agent execution',
      inputTokens: 88000, outputTokens: 22000, cacheReadTokens: 0, cacheWriteTokens: 0,
      calculatedAt: '2026-06-09T10:00:00Z',
      estimate: {
        inputUsd: 4.8, outputUsd: 1.2, cacheReadUsd: 0, cacheWriteUsd: 0, total: 6,
        modelId: 'claude-opus-4-8', modelKnown: true, status: 'resolved',
        priceBasis: {
          inputPerMillion: 5, outputPerMillion: 25, cacheReadPerMillion: 0.5,
          cacheWritePerMillion: 6.25, currency: 'USD', validFrom: '2026-01-01T00:00:00Z',
          source: 'Anthropic published pricing', note: null, unconfirmed: false,
        },
      },
    }],
  })));
}

async function saveShot(page: Page, name: string) {
  const buf = await page.screenshot({ fullPage: false });
  if (!RESULTS_DIR) return;
  await mkdir(RESULTS_DIR, { recursive: true });
  await writeFile(join(RESULTS_DIR, name), buf);
}

test('token usage: each pipeline step surfaces its own usage, without the aggregate model block', async ({ page }) => {
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page);

  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  const pipeline = page.getByTestId('overview-pipeline');
  await expect(pipeline).toBeVisible({ timeout: 10000 });
  await expect(page.getByTestId('overview-pipeline-tokens-by-model')).toHaveCount(0);
  await page.getByText('CORE AGENT WORK', { exact: true }).click();
  await page.getByText('ASPECT', { exact: true }).click();

  const coreRow = page.locator('[data-step-id="core-agent-run"]');
  const aspectRow = page.locator('[data-step-id="aspect-code-quality"]');
  await expect(coreRow.getByTestId('overview-pipeline-step-tokens')).toHaveText(/110(?:\.0)?k/i);
  await expect(coreRow.getByTestId('overview-pipeline-step-cost')).toContainText('$0.75');
  await expect(aspectRow.getByTestId('overview-pipeline-step-tokens')).toContainText('1.2m');
  await expect(aspectRow.getByTestId('overview-pipeline-step-cost')).toContainText('$2.00');
  await expect(page.getByTestId('overview-pipeline-total-tokens')).toContainText('1.3m');
  await expect(page.getByTestId('overview-pipeline-total-cost')).toContainText('$2.75');

  await coreRow.getByTestId('overview-pipeline-step-tokens').hover();
  await expect(page.getByTestId('cac-tooltip')).toContainText('Estimated - historical list prices');
  await saveShot(page, 'pipeline-token-cost-tooltip--mocked.png');

  await pipeline.screenshot({
    path: RESULTS_DIR ? join(RESULTS_DIR, 'pipeline-step-usage--mocked.png') : 'test-results/pipeline-step-usage--mocked.png',
  });

  await aspectRow.getByTestId('overview-pipeline-step-tokens').click();
  const dialog = page.getByTestId('overview-step-token-modal');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('Code quality');
  await expect(dialog).toContainText('Input');
  await expect(dialog).toContainText('960k');
  await expect(dialog).toContainText('Output');
  await expect(dialog).toContainText('240k');
  await expect(dialog).toContainText('1.2m');

  await dialog.getByRole('button', { name: 'Close' }).last().click();
  await coreRow.getByTestId('overview-pipeline-step-cost').click();
  const costDialog = page.getByTestId('cost-breakdown-dialog');
  await expect(costDialog).toBeVisible();
  await expect(costDialog).toContainText('claude-opus-4-8');
  await expect(costDialog).toContainText('Input / 1M');
  await expect(costDialog.getByTestId('cost-breakdown-formula')).toContainText('/ 1M × $5.00');
  await expect(costDialog).toContainText('Anthropic published pricing');
  await expect(costDialog).toContainText('Price effective date');
  await saveShot(page, 'cost-breakdown-dialog-light--mocked.png');
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  await saveShot(page, 'cost-breakdown-dialog-dark--mocked.png');
});

test('post-step lifecycle shows backend activation, history, and the exact settings row in both themes', async ({ page }) => {
  await page.addInitScript(() => {
    try { localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false })); }
    catch { /* ignore */ }
  });
  await installFixtureRoutes(page);
  await page.route('**/api/projects/pipeline-catalogue**', route => route.fulfill(json({ steps: [{
    id: 'post-wiki-learnings', displayName: 'Wiki learnings', kind: 'tool', phase: 'post',
    runMode: 'sequential', dependsOn: ['aspect-code-quality'], idempotent: true, stub: false,
    usesModel: false, usesPrompt: false, supportsMode: false, canDisable: true,
    defaultEnabled: false, supportsCondition: false,
  }] })));
  await page.route('**/api/projects/settings', route => route.fulfill(json({
    'agent-taskboard': { pipelineSteps: { 'post-wiki-learnings': { enabled: false } } },
  })));
  let activationBody: unknown = null;
  await page.route('**/api/projects/agent-taskboard/pipeline-step', async route => {
    activationBody = route.request().postDataJSON();
    await route.fulfill(json({
      stepId: 'post-wiki-learnings',
      pipelineSteps: { 'post-wiki-learnings': { enabled: true } },
    }));
  });
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await page.getByText('TOOL', { exact: true }).click();
  await page.getByText('ASPECT', { exact: true }).click();
  const maintenance = page.locator('[data-step-id="post-wiki-maintenance"]');
  const learnings = page.locator('[data-step-id="post-wiki-learnings"]');
  const agentsSync = page.locator('[data-step-id="post-agents-wiki-sync"]');
  const aspect = page.locator('[data-step-id="aspect-code-quality"]');
  await expect(maintenance.getByTestId('overview-post-step-source')).toHaveText('active·condition');
  await expect(maintenance.getByTestId('overview-post-step-source')).toHaveAttribute(
    'aria-label',
    'active from condition: Enabled by the global catalogue default; condition "task has tag \'wiki\'" matched this run. Open settings.',
  );
  await expect(maintenance.getByTestId('overview-post-step-run')).toHaveText('Run again');
  await expect(maintenance.getByTestId('overview-post-step-attempts')).toContainText('2 attempts');
  await maintenance.getByTestId('overview-post-step-attempts').click();
  await expect(maintenance.getByTestId('overview-post-step-attempt-row')).toHaveCount(2);
  await expect(maintenance.getByTestId('overview-post-step-attempt-row').first()).toContainText('#2');
  await expect(maintenance.getByTestId('overview-post-step-artifact').first()).toContainText('attempt-002.md');
  await expect(learnings.getByTestId('overview-post-step-source')).toHaveText('inactive·project');
  await expect(learnings.getByTestId('overview-post-step-run')).toHaveText('Add + run');
  await expect(agentsSync.getByTestId('overview-post-step-source')).toHaveText('skipped·condition');
  await expect(agentsSync.getByTestId('overview-post-step-source')).toHaveAttribute(
    'aria-label',
    'skipped from condition: Condition "an aspect failed" did not match this run. Open settings.',
  );
  await expect(aspect.getByTestId('overview-post-step-source')).toHaveText('active·global');
  await expect(aspect.getByTestId('overview-post-step-run')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(t => { document.documentElement.dataset['studioTheme'] = t; }, theme);
    const path = RESULTS_DIR
      ? join(RESULTS_DIR, `post-step-lifecycle-${theme}--mocked.png`)
      : `test-results/post-step-lifecycle-${theme}--mocked.png`;
    await page.getByTestId('overview-pipeline').screenshot({ path });
  }

  await maintenance.getByTestId('overview-post-step-attempts').click();
  await expect(maintenance.getByTestId('overview-post-step-attempt-row')).toHaveCount(0);
  await learnings.getByTestId('overview-post-step-source').click();
  const activationRow = page.getByTestId('pipeline-step-row-post-wiki-learnings');
  await expect(page.getByTestId('project-detail-pipeline')).toBeVisible();
  await expect(activationRow).toBeVisible();
  await expect(activationRow).toHaveAttribute('aria-current', 'location');
  await expect(activationRow).toHaveJSProperty('open', true);
  await page.getByTestId('pipeline-step-enabled-post-wiki-learnings').check();
  await expect.poll(() => activationBody).toMatchObject({ stepId: 'post-wiki-learnings', enabled: true });
});

test('retro grading stays available on an existing card and is captured in both themes', async ({ page }) => {
  await page.addInitScript(() => {
    try { localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false })); }
    catch { /* ignore */ }
  });
  await installFixtureRoutes(page);
  await page.route('**/api/tasks/code-review/defaults', route => route.fulfill(json({ cliType: 'claude', model: 'claude-opus-4-8' })));
  await page.route(`**/api/tasks/${JOB_ID}/code-review/list**`, route => route.fulfill(json({ entries: [{
    fileName: 'code-review-grade-2026-07-11T22-15-00Z.md', verdict: 'pass', grade: 'B',
    summary: 'Solid result with one small evidence gap.', model: 'claude-opus-4-8', cliType: 'claude',
    commit: 'base..task/pipeline-step-usage-fixture', runAt: '2026-07-11T22:15:00Z',
    inputTokens: 125000, outputTokens: 18000, cacheReadTokens: 42000, cacheCreationTokens: 6000,
    totalTokens: 191000, estimatedApiCostUsd: 1.42, priceKnown: true,
  }] })));
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.getByTestId('prompt-tab-code-review').click();

  await expect(page.getByTestId('code-review-grade-run')).toBeVisible();
  await expect(page.getByTestId('code-review-list')).toContainText('Grade B');
  const usage = page.getByTestId('code-review-token-usage');
  await expect(usage).toContainText('125k in / 18k out (191k) tokens');
  await usage.hover();
  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toContainText('Estimated cost: $1.42');
  await expect(tooltip).toContainText('Estimated - historical list prices');
  await saveShot(page, 'review-token-cost-tooltip--mocked.png');
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(t => { document.documentElement.dataset['studioTheme'] = t; }, theme);
    const path = RESULTS_DIR
      ? join(RESULTS_DIR, `retro-grading-${theme}--mocked.png`)
      : `test-results/retro-grading-${theme}--mocked.png`;
    await page.getByTestId('code-review-panel').screenshot({ path });
  }
});
