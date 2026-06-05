import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Pre-run model visibility + editing (hierarchical per-step model config).
 *
 * Before any run has recorded a model, each LLM-backed pipeline step shows the
 * effective model it WILL run on, resolved by the backend the same way the
 * runtime resolves it (step override -> project model -> global -> catalogue ->
 * runtime default). The resolved chip renders with `data-model-resolved="true"`
 * and a dashed "will run on" style so the operator can see, and reason about,
 * the model hierarchy ahead of the run.
 *
 * The aspect review rows additionally expose an inline model selector that
 * writes a project-level per-step override via PUT /pipeline-step and re-
 * resolves the pipeline, so the operator can change the model a step will run
 * on, in context, before starting — the second test exercises that loop.
 *
 * Fully mocked - no backend or git repository needed. `execution` is null
 * (nothing has run yet), so the only model source is the resolved config.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-prerun-model';
const JOB_ID = 'pipeline-prerun-model-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Pre-run model fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Test prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

function step(id: string, displayName: string, kind: string, runMode: string) {
  return { id, displayName, kind, runMode, dependsOn: [], idempotent: true, stub: false };
}

const pre = [step('pre-loop-guard', 'Loop guard', 'module', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [
  step('aspect-code-quality', 'Code quality', 'aspect', 'parallel'),
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential'),
];
const allSteps = [...pre, ...core, ...post];

function basePipeline() {
  return {
    id: 'standard-task-pipeline',
    displayName: 'Standard task pipeline',
    version: 1,
    pre,
    core,
    post,
    allSteps,
  };
}

/**
 * Config block exactly as the backend emits it pre-run: the two aspect steps
 * resolve a model (one from the project override, one with a per-step override),
 * deterministic / core steps resolve none.
 */
function prerunConfig(): Record<string, Record<string, unknown>> {
  return {
    'aspect-code-quality': {
      enabled: true,
      model: 'claude-opus-4-7',
      thinkingLevel: null,
      mode: null,
      resolvedModel: 'claude-opus-4-7',
      modelSource: 'step',
    },
    'aspect-requirement-fit': {
      enabled: true,
      model: null,
      thinkingLevel: null,
      mode: null,
      resolvedModel: 'claude-sonnet-4-6',
      modelSource: 'project',
    },
  };
}

function prerunPipeline(config: Record<string, unknown>) {
  return {
    pipeline: basePipeline(),
    execution: null,
    cost: {
      steps: [],
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0,
      totalTokens: 0,
      totalInputCostUsd: 0,
      totalOutputCostUsd: 0,
      totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0,
      totalCostUsd: 0,
      anyModelUnknown: false,
    },
    config,
  };
}

/**
 * Mutable backend state captured by the routes so a per-step model write
 * (PUT /pipeline-step) is reflected by the next pipeline GET — exercising the
 * full edit -> persist -> re-resolve loop the Overview selector drives.
 */
interface MockState {
  config: Record<string, Record<string, unknown>>;
  pipelineStepPuts: Array<Record<string, unknown>>;
}

function makeMockState(): MockState {
  return { config: prerunConfig(), pipelineStepPuts: [] };
}

async function installRoutes(page: Page, state: string, mock: MockState): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const projEsc = PROJECT.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [], orchestratorPrep: [], ready: [],
        progress: [], failedPickup: [], autoReview: [], humanReview: [],
        completed: [], archive: [],
      }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
      ]),
    }),
  );
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      }),
    }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-06-02T00:00:00Z', snapshots: [] }),
    }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'auto',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }),
  );

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(prerunPipeline(mock.config)),
    }),
  );
  // Per-step model write. Records the request, mutates the captured config so
  // the next pipeline GET re-resolves with the new override (source "step"),
  // mirroring the backend's replace-then-resolve behaviour.
  await page.route(new RegExp(`/api/projects/${projEsc}/pipeline-step(\\?|$)`), async (route) => {
    const body = route.request().postDataJSON() as Record<string, unknown>;
    mock.pipelineStepPuts.push(body);
    const stepId = String(body.stepId);
    const model = (body.model as string | null) ?? null;
    if (mock.config[stepId]) {
      mock.config[stepId].model = model;
      mock.config[stepId].resolvedModel = model ?? 'claude-sonnet-4-6';
      mock.config[stepId].modelSource = model ? 'step' : 'project';
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ stepId, pipelineSteps: {} }),
    });
  });
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

test.describe('Pipeline: pre-run resolved model', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: false, git: false }),
        );
      } catch {
        /* private mode */
      }
    });
  });

  test('each LLM step shows its effective model before the run, marked as resolved', async ({ page }) => {
    await installRoutes(page, '2-ready', makeMockState());
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // The aspect rows render the resolved model even though nothing has run.
    const cqRow = page.locator('[data-step-id="aspect-code-quality"]');
    const cqModel = cqRow.getByTestId('overview-pipeline-step-model');
    await expect(cqModel).toHaveText('claude-opus-4-7');
    await expect(cqModel).toHaveAttribute('data-model-resolved', 'true');

    const rfRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    const rfModel = rfRow.getByTestId('overview-pipeline-step-model');
    await expect(rfModel).toHaveText('claude-sonnet-4-6');
    await expect(rfModel).toHaveAttribute('data-model-resolved', 'true');

    // Each aspect row also carries an inline selector reflecting its stored
    // override: code-quality has a per-step override (opus), requirement-fit
    // inherits the project model (empty = Inherit).
    await expect(cqRow.getByTestId('overview-pipeline-step-model-select')).toHaveValue('claude-opus-4-7');
    await expect(rfRow.getByTestId('overview-pipeline-step-model-select')).toHaveValue('');

    // Deterministic steps resolve no model, so neither chip nor selector renders.
    const loopRow = page.locator('[data-step-id="pre-loop-guard"]');
    await expect(loopRow.getByTestId('overview-pipeline-step-model')).toHaveCount(0);
    await expect(loopRow.getByTestId('overview-pipeline-step-model-select')).toHaveCount(0);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-prerun-model.png'),
        fullPage: true,
      });
    }
  });

  test('changing an aspect step model before the run persists the override and re-resolves the chip', async ({ page }) => {
    const mock = makeMockState();
    await installRoutes(page, '2-ready', mock);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    const rfRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    const rfModel = rfRow.getByTestId('overview-pipeline-step-model');
    const rfSelect = rfRow.getByTestId('overview-pipeline-step-model-select');

    // Starts on the inherited project model (sonnet, source project).
    await expect(rfModel).toHaveText('claude-sonnet-4-6');
    await expect(rfSelect).toHaveValue('');

    // Pin the step to Opus before the run.
    await rfSelect.selectOption('claude-opus-4-7');

    // The write hit the project pipeline-step endpoint with the new model and
    // the model's default thinking level, leaving the other facets cleared.
    await expect.poll(() => mock.pipelineStepPuts.length).toBeGreaterThan(0);
    const put = mock.pipelineStepPuts.at(-1)!;
    expect(put.stepId).toBe('aspect-requirement-fit');
    expect(put.model).toBe('claude-opus-4-7');
    expect(put.thinkingLevel).toBe('high');
    expect(put.enabled).toBeNull();
    expect(put.mode).toBeNull();

    // After the re-resolve, the chip reflects the pinned model and now reads as
    // a per-step override (still pre-run, so still the dashed "will run on").
    await expect(rfModel).toHaveText('claude-opus-4-7');
    await expect(rfModel).toHaveAttribute('data-model-resolved', 'true');
    await expect(rfSelect).toHaveValue('claude-opus-4-7');

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-prerun-model-edited.png'),
        fullPage: true,
      });
    }
  });
});
