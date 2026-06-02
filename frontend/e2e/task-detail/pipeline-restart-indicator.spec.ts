import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Restart visibility: a re-run / re-issue is unmistakably a NEW run, and the
 * prior run's steps stay distinguishable from the current ones.
 *
 * Acceptance (Sub-Task von [EPIC] Pipeline-Tabelle, "Pipeline-Neustart
 * sichtbar machen"): "Wenn die Pipeline neu gestartet wurde (neuer
 * Run/Attempt), muss das in der Tabelle/Timeline ersichtlich sein ... ein
 * Re-Run ist klar als neuer Durchlauf erkennbar, alte und neue Step-Laeufe
 * sind unterscheidbar." The backend bumps `attempt` and archives the prior
 * run into `previousAttempts` in `pipeline-execution.json`; the Overview
 * pipeline block renders a "Restarted - Run #N" badge plus a "Previous runs"
 * strip. This spec covers that visual contract.
 *
 * Fully mocked - no backend or git repository needed.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-restart';
const JOB_ID = 'pipeline-restart-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Restart indicator fixture',
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
  step('aspect-code-quality', 'Code quality', 'aspect'),
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

function execStep(stepId: string, kind: string, status: string) {
  const started = status === 'pending' ? null : '2026-06-02T08:00:00Z';
  const completed = status === 'passed' || status === 'failed' ? '2026-06-02T08:00:42Z' : null;
  return {
    stepId,
    kind,
    status,
    durationMs: completed ? 42_000 : 0,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: started,
    completedAt: completed,
  };
}

// A pristine first run: attempt 1, no archived history -> no restart badge.
function pipelineFirstRun() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: null,
      attempt: 1,
      previousAttempts: [],
      steps: [
        execStep('core-agent-run', 'core', 'running'),
        execStep('aspect-code-quality', 'aspect', 'pending'),
        execStep('post-orchestrator-decision', 'orchestrator', 'pending'),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

// A restarted run: attempt 2, with the first run archived. The current run's
// steps are fresh; the archived run records the prior outcomes (one failure)
// so old vs. new is distinguishable.
function pipelineRestarted() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T09:30:00Z',
      completedAt: null,
      attempt: 2,
      previousAttempts: [
        {
          pipelineId: 'standard-task-pipeline',
          pipelineVersion: 1,
          jobId: JOB_ID,
          project: PROJECT,
          startedAt: '2026-06-02T08:00:00Z',
          completedAt: '2026-06-02T08:05:00Z',
          attempt: 1,
          previousAttempts: [],
          steps: [
            execStep('core-agent-run', 'core', 'passed'),
            execStep('aspect-code-quality', 'aspect', 'failed'),
            execStep('post-orchestrator-decision', 'orchestrator', 'passed'),
          ],
        },
      ],
      steps: [
        execStep('core-agent-run', 'core', 'running'),
        execStep('aspect-code-quality', 'aspect', 'pending'),
        execStep('post-orchestrator-decision', 'orchestrator', 'pending'),
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

test.describe('Pipeline restart indicator', () => {
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

  test('a first run shows no restart badge and no previous-runs strip', async ({ page }) => {
    await installRoutes(page, '3-progress', pipelineFirstRun);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // Attempt 1, empty archive -> the restart affordances stay hidden.
    await expect(page.getByTestId('overview-pipeline-restart-badge')).toHaveCount(0);
    await expect(page.getByTestId('overview-pipeline-previous-runs')).toHaveCount(0);
  });

  test('a restarted run is flagged and the prior run stays distinguishable', async ({ page }) => {
    await installRoutes(page, '3-progress', pipelineRestarted);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // The restart badge flags the current run as a fresh attempt (#2).
    const badge = page.getByTestId('overview-pipeline-restart-badge');
    await expect(badge).toBeVisible();
    await expect(badge).toHaveAttribute('data-attempt', '2');
    await expect(badge).toContainText('Run #2');

    // The prior run is preserved as a distinct, scannable entry: old vs. new
    // step runs are distinguishable without re-reading the full step list.
    const history = page.getByTestId('overview-pipeline-previous-runs');
    await expect(history).toBeVisible();
    const priorRuns = page.getByTestId('overview-pipeline-previous-run');
    await expect(priorRuns).toHaveCount(1);
    await expect(priorRuns.first()).toHaveAttribute('data-attempt', '1');
    await expect(priorRuns.first()).toContainText('Run #1');
    // The archived run recorded two passes and one failure.
    await expect(priorRuns.first()).toContainText('2✓');
    await expect(priorRuns.first()).toContainText('1✗');

    // The current run's own steps are the live ones (core still running).
    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    await expect(coreRow).toHaveAttribute('data-status', 'running');

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-restart-indicator.png'),
        fullPage: true,
      });
    }
  });
});
