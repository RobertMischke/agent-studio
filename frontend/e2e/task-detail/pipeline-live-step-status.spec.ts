import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Live step status: the currently RUNNING pipeline step is unmistakable.
 *
 * Acceptance (Sub-Task von [EPIC] Pipeline-Tabelle): "laufender Step ist in
 * der Tabelle eindeutig als aktiv erkennbar, aktualisiert sich live." The
 * backend records a step as `running` in `pipeline-execution.json` at spawn
 * (ProjectRunner / AspectRunnerService / DriftPostStepRunner) and the
 * Overview pipeline block polls `/api/tasks/{id}/pipeline` every 10s, so the
 * data is already live. This spec covers the visual contract: the active
 * step's row carries `data-status="running"`, a "Running" badge, and that
 * exactly one step lights up at a time. A second mocked snapshot proves the
 * highlight moves to the next step when the poll refreshes (the live update).
 *
 * Fully mocked - no backend or git repository needed; the pipeline block is
 * generic and renders one row per step from the joined catalogue + execution.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-live';
const JOB_ID = 'pipeline-live-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Live step status fixture',
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

function step(id: string, displayName: string, kind: string) {
  return {
    id,
    displayName,
    kind,
    runMode: 'sequential',
    dependsOn: [],
    idempotent: true,
    stub: false,
  };
}

const core = [step('core-agent-run', 'Agent run', 'core')];
const post = [
  step('post-lint-scss', 'Frontend stylelint', 'tool'),
  step('post-orchestrator-decision', 'Auto-review decision', 'orchestrator'),
];
const allSteps = [...core, ...post];

function basePipeline() {
  return {
    id: 'standard-task-pipeline',
    displayName: 'Standard task pipeline',
    version: 1,
    pre: [],
    core,
    post,
    allSteps,
  };
}

function execStep(stepId: string, kind: string, status: string, durationMs: number) {
  return {
    stepId,
    kind,
    status,
    durationMs,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: status === 'pending' ? null : '2026-06-02T08:00:00Z',
    completedAt: status === 'running' || status === 'pending' ? null : '2026-06-02T08:00:42Z',
  };
}

// Snapshot 1: the core agent step is in flight; the post steps have not been
// reached yet.
function pipelineRunningCore() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: null,
      steps: [
        execStep('core-agent-run', 'core', 'running', 0),
        execStep('post-lint-scss', 'tool', 'pending', 0),
        execStep('post-orchestrator-decision', 'orchestrator', 'pending', 0),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

// Snapshot 2 (after the poll refreshes): core is done, the lint step is now
// the one running. Proves the highlight moves live with the data.
function pipelineRunningLint() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: null,
      steps: [
        execStep('core-agent-run', 'core', 'passed', 42_000),
        execStep('post-lint-scss', 'tool', 'running', 0),
        execStep('post-orchestrator-decision', 'orchestrator', 'pending', 0),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

async function installRoutes(page: Page, state: string, pipelineBody: () => unknown) {
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
        preparation: [], orchestratorPrep: [], needsHumanReview: [], ready: [],
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
            mode: 'manual',
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

test.describe('Pipeline live step status', () => {
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

  test('the running step is marked active and unique', async ({ page }) => {
    await installRoutes(page, '3-progress', pipelineRunningCore);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // The in-flight step carries the running status and an explicit badge.
    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    await expect(coreRow).toHaveAttribute('data-status', 'running');
    await expect(coreRow.getByTestId('overview-pipeline-step-running')).toBeVisible();
    await expect(coreRow.getByTestId('overview-pipeline-step-running')).toContainText('Running');

    // Exactly one step is active at a time: the not-yet-reached steps are
    // pending and carry no running badge.
    await expect(page.getByTestId('overview-pipeline-step-running')).toHaveCount(1);
    const lintRow = page.locator('[data-step-id="post-lint-scss"]');
    await expect(lintRow).toHaveAttribute('data-status', 'pending');

    if (RESULTS_DIR) {
      await coreRow.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-live-running-core.png'),
        fullPage: true,
      });
    }
  });

  test('the active highlight moves to the next step when the poll refreshes', async ({ page }) => {
    // First load: core is running. Then swap the route to the next snapshot
    // and let the 10s pipeline poll pick it up so the highlight migrates.
    await installRoutes(page, '3-progress', pipelineRunningCore);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    await expect(coreRow).toHaveAttribute('data-status', 'running', { timeout: 10_000 });

    const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    await page.unroute(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`));
    await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(pipelineRunningLint()),
      }),
    );

    // The poll runs every 10s; wait for the highlight to migrate.
    const lintRow = page.locator('[data-step-id="post-lint-scss"]');
    await expect(lintRow).toHaveAttribute('data-status', 'running', { timeout: 15_000 });
    await expect(coreRow).toHaveAttribute('data-status', 'passed');
    await expect(page.getByTestId('overview-pipeline-step-running')).toHaveCount(1);
  });
});
