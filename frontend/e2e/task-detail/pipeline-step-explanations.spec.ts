import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Job-Details pipeline: every step name carries a "what happens here"
 * explanation tooltip.
 *
 * Acceptance ("Hover-Erklaerungen fuer alle Pipeline-Steps"): hovering a step
 * name in the Overview pipeline opens the canonical app-tooltip with the step
 * label as title and a per-step explanation as body, so a user understands what
 * PRE / CORE / ASPECT / TOOL / DECISION / DRIFT each do without leaving the
 * Overview.
 *
 * Fully mocked - no backend or git repository needed; the pipeline block is
 * generic and renders one row per step from the joined catalogue + execution.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-step-explanations';
const JOB_ID = 'pipeline-step-explanations-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Step explanations fixture',
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

// One step per kind so the explanation copy is exercised across PRE / CORE /
// ASPECT / TOOL / DECISION / DRIFT.
const pre = [step('pre-loop-guard', 'Loop check', 'module', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('post-git-commit-attribution', 'Commit attribution', 'tool', 'sequential'),
  step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential'),
  step('post-drift-adr-code', 'ADR vs code drift', 'drift', 'sequential'),
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

function execStep(stepId: string, kind: string, status: string, extra: Record<string, unknown> = {}) {
  return {
    stepId,
    kind,
    status,
    durationMs: 1_500,
    inputTokens: 800,
    outputTokens: 200,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: status === 'pending' ? null : '2026-06-02T08:00:00Z',
    completedAt: status === 'running' || status === 'pending' ? null : '2026-06-02T08:00:02Z',
    ...extra,
  };
}

function pipelineBody() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: '2026-06-02T08:00:08Z',
      steps: [
        execStep('pre-loop-guard', 'module', 'passed'),
        execStep('core-agent-run', 'core', 'passed'),
        execStep('aspect-requirement-fit', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('post-git-commit-attribution', 'tool', 'passed'),
        execStep('post-orchestrator-decision', 'orchestrator', 'passed', { verdict: 'accept' }),
        execStep('post-drift-adr-code', 'drift', 'passed'),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

async function installRoutes(page: Page, state: string) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
  await page.route('**/api/tasks', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
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
  await page.route('**/api/workspaces**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/projects**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }),
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
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-06-02T00:00:00Z', snapshots: [] }),
    }),
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
            activeJobId: JOB_ID,
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
      body: JSON.stringify(pipelineBody()),
    }),
  );
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

test.describe('Pipeline: per-step explanation tooltips', () => {
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

  test('every step name opens an explanation tooltip with the step label as title', async ({ page }) => {
    await installRoutes(page, '4-auto-review');
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // One explanation anchor per rendered pipeline step (6 here).
    const names = page.getByTestId('overview-pipeline-step-name');
    await expect(names).toHaveCount(6);

    const tooltip = page.getByTestId('app-tooltip');

    // CORE: hovering the agent-run step explains the single coding seat.
    await page.locator('[data-step-id="core-agent-run"]')
      .getByTestId('overview-pipeline-step-name').hover();
    await expect(tooltip).toBeVisible();
    await expect(tooltip.locator('.app-tooltip__title')).toHaveText('Agent execution');
    await expect(tooltip.locator('.app-tooltip__body')).toContainText('coding seat');

    // PRE: the loop guard explanation mentions the loop guard.
    await page.locator('[data-step-id="pre-loop-guard"]')
      .getByTestId('overview-pipeline-step-name').hover();
    await expect(tooltip.locator('.app-tooltip__body')).toContainText('loop guard');

    // ASPECT: the requirement-fit aspect explanation mentions acceptance criteria.
    await page.locator('[data-step-id="aspect-requirement-fit"]')
      .getByTestId('overview-pipeline-step-name').hover();
    await expect(tooltip.locator('.app-tooltip__body')).toContainText('acceptance criteria');

    // TOOL: the commit-attribution step explanation mentions git commits.
    await page.locator('[data-step-id="post-git-commit-attribution"]')
      .getByTestId('overview-pipeline-step-name').hover();
    await expect(tooltip.locator('.app-tooltip__body')).toContainText('git commits');

    // DECISION: the orchestrator decision explanation mentions the final ruling.
    await page.locator('[data-step-id="post-orchestrator-decision"]')
      .getByTestId('overview-pipeline-step-name').hover();
    await expect(tooltip.locator('.app-tooltip__body')).toContainText('final ruling');

    // DRIFT: the drift step explanation flags that it is off by default.
    await page.locator('[data-step-id="post-drift-adr-code"]')
      .getByTestId('overview-pipeline-step-name').hover();
    await expect(tooltip.locator('.app-tooltip__body')).toContainText('off by default');

    if (RESULTS_DIR) {
      // Keep the last tooltip open for the capture.
      await page.locator('[data-step-id="core-agent-run"]')
        .getByTestId('overview-pipeline-step-name').hover();
      await expect(tooltip).toBeVisible();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-step-explanation-tooltip.png'),
        fullPage: true,
      });
    }
  });
});
