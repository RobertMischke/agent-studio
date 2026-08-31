const { chromium } = require('playwright');
const path = require('path');

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-column-headers';
const JOB_ID = 'pipeline-column-headers-test';
const resultsDir = process.env.JOB_RESULTS_DIR;

const step = (id, displayName, kind, runMode) => ({
  id, displayName, kind, runMode, dependsOn: [], idempotent: true, stub: false,
});
const execStep = (stepId, kind, model, extra = {}) => ({
  stepId, kind, status: 'passed', model, durationMs: 92_000,
  inputTokens: 8_000, outputTokens: 2_000, cacheReadTokens: 0,
  cacheCreationTokens: 0, startedAt: '2026-06-02T08:00:00Z',
  completedAt: '2026-06-02T08:01:32Z', ...extra,
});
const costStep = (stepId, model, totalTokens, costUsd) => ({
  stepId, model, modelKnown: true, tokenUsageSource: 'orchestrator',
  inputTokens: Math.round(totalTokens * 0.8), outputTokens: Math.round(totalTokens * 0.2),
  cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens,
  inputCostUsd: costUsd * 0.6, outputCostUsd: costUsd * 0.4,
  cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd,
});

const pre = [step('pre-loop-guard', 'Loop guard', 'module', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const aspects = [
  step('aspect-requirement-fit', 'Requirement alignment and operator acceptance criteria', 'aspect', 'parallel'),
  step('aspect-code-quality', 'Code quality and maintainability review', 'aspect', 'parallel'),
  step('aspect-security', 'Security boundary and dependency review', 'aspect', 'parallel'),
  step('aspect-ux-quality', 'User experience and visual quality review', 'aspect', 'parallel'),
];
const post = [
  ...aspects,
  step('post-git-commit-attribution', 'Git attribution', 'tool', 'sequential'),
  step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential'),
  step('post-drift-adr-code', 'ADR drift', 'drift', 'sequential'),
];
const pipelineBody = {
  pipeline: {
    id: 'standard-task-pipeline', displayName: 'Standard task pipeline', version: 1,
    pre, core, post, allSteps: [...pre, ...core, ...post],
  },
  execution: {
    pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: JOB_ID,
    project: PROJECT, startedAt: '2026-06-02T08:00:00Z', completedAt: '2026-06-02T08:05:00Z',
    steps: [
      execStep('pre-loop-guard', 'module', 'claude-haiku-4-5'),
      execStep('core-agent-run', 'core', 'claude-opus-4-7'),
      execStep('aspect-requirement-fit', 'aspect', 'claude-haiku-4-5', { verdict: 'block', verdictSummary: 'Acceptance evidence is incomplete.' }),
      execStep('aspect-code-quality', 'aspect', 'claude-haiku-4-5', { verdict: 'concerns', verdictSummary: 'One maintainability concern remains.' }),
      execStep('aspect-security', 'aspect', 'claude-haiku-4-5', { verdict: 'pass' }),
      execStep('aspect-ux-quality', 'aspect', 'claude-haiku-4-5', { verdict: 'pass' }),
      execStep('post-git-commit-attribution', 'tool', 'claude-haiku-4-5'),
      execStep('post-orchestrator-decision', 'orchestrator', 'claude-haiku-4-5', { verdict: 'accept' }),
      execStep('post-drift-adr-code', 'drift', 'claude-haiku-4-5', { verdict: 'clean' }),
    ],
  },
  cost: {
    steps: [
      costStep('pre-loop-guard', 'claude-haiku-4-5', 1_200, 0.0021),
      costStep('core-agent-run', 'claude-opus-4-7', 248_000, 4.37),
      costStep('aspect-requirement-fit', 'claude-haiku-4-5', 20_000, 0.0300),
      costStep('aspect-code-quality', 'claude-haiku-4-5', 24_000, 0.0360),
      costStep('aspect-security', 'claude-haiku-4-5', 25_000, 0.0375),
      costStep('aspect-ux-quality', 'claude-haiku-4-5', 26_800, 0.0402),
      costStep('post-git-commit-attribution', 'claude-haiku-4-5', 800, 0.0010),
      costStep('post-orchestrator-decision', 'claude-haiku-4-5', 5_400, 0.0089),
      costStep('post-drift-adr-code', 'claude-haiku-4-5', 12_000, 0.0180),
    ],
    totalInputTokens: 290_560, totalOutputTokens: 72_640,
    totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 363_200,
    totalInputCostUsd: 2.7, totalOutputCostUsd: 1.8,
    totalCacheReadCostUsd: 0, totalCacheCreationCostUsd: 0,
    totalCostUsd: 4.54, anyModelUnknown: false,
  },
  config: {},
};
const detail = {
  info: {
    id: JOB_ID, taskKey: `${WATCH_PATH}::${JOB_ID}`, title: 'Narrow pipeline fixture',
    state: '4-auto-review', agent: 'claude', cliType: 'claude', model: 'claude-opus-4-7',
    watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/4-auto-review/${JOB_ID}`,
    sessionName: null, lastUsage: null, execution: null, order: 1,
    commit: null, commits: [], ownerClientId: 'local-default',
  },
  promptMarkdown: 'Test prompt.', statusMarkdown: '', log: [], promptHistory: [],
  contextUsage: null, reviewEvidence: [],
  summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
};

(async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {}));
  const json = body => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/auth/status', route => route.fulfill(json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null })));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json({ preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], autoReview: [], humanReview: [], completed: [], archive: [] })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }])));
  await page.route('**/api/environment**', route => route.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ at: '2026-06-02T00:00:00Z', snapshots: [] })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ items: [] })));
  await page.route('**/api/projects/*/workbenches**', route => route.fulfill(json({ projectName: PROJECT, includesHistory: true, count: 0, items: [] })));
  await page.route(/\/api\/runner\/status(\?|$)/, route => route.fulfill(json({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'auto', activeJobId: JOB_ID, activeExecution: null, queuedJobIds: [] } } })));
  await page.route(`**/api/tasks/${JOB_ID}/output**`, route => route.fulfill(json([])));
  await page.route(`**/api/tasks/${JOB_ID}/runs**`, route => route.fulfill(json({ runs: [] })));
  await page.route(`**/api/tasks/${JOB_ID}/session-events**`, route => route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(`**/api/tasks/${JOB_ID}/pipeline**`, route => route.fulfill(json(pipelineBody)));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}(\\?|$)`), route => route.fulfill(json(detail)));

  await page.goto(`http://localhost:4011/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.locator('[data-testid="overview-pipeline-steps"]').waitFor({ state: 'visible', timeout: 15_000 });
  await page.addStyleTag({ content: '.ov-pipeline { width: 430px !important; max-width: 430px !important; }' });
  const aspect = page.locator('[data-testid="overview-pipeline-phase"][data-phase="aspect"]');
  if (await aspect.getAttribute('aria-expanded') === 'false') await aspect.click();
  await page.locator('[data-testid="overview-pipeline-steps"]').scrollIntoViewIfNeeded();
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => {
      document.documentElement.dataset.studioTheme = value;
      localStorage.setItem('atp.studio.theme', value);
    }, theme);
    await page.screenshot({ path: path.join(resultsDir, `pipeline-narrow-before-${theme}--mocked.png`), fullPage: true });
  }
  await browser.close();
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
