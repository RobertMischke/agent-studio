import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * BUG fix evidence: the two orchestrator-review pipeline rows must be visibly
 * DISTINCT and the "Auto-review accepted …" copy must not be duplicated.
 *
 * Before this fix the Overview pipeline showed:
 *  - a top "Note: Auto-review accepted … Moved to 5-human-review" banner,
 *  - the post-core `post-orchestrator-review` row with a "Final verdict" chip,
 *  - the final `post-orchestrator-decision` row with a second "Final verdict" chip,
 * i.e. two "FINAL VERDICT" rows + a redundant top NOTE, both labelled
 * "Orchestrator-Review".
 *
 * After the fix:
 *  - EXACTLY one "Final verdict" chip (on the final decision row only),
 *  - the post-core review row carries its own early-gate result, NOT a final verdict,
 *  - the two rows carry DISTINCT display names ("Post-Core Orchestrator-Review"
 *    vs "Final Orchestrator-Review"),
 *  - PRE / CORE / ASPECT / TOOL / DECISION / DRIFT group headers split the list,
 *  - the accepted completion-loop strip shows no redundant "Note" banner.
 *
 * Fully mocked — no backend or git repository needed.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-orch-review-distinct';
const JOB_ID = 'pipeline-orch-review-distinct';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Orchestrator-review distinct fixture',
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

const pre = [step('pre-loop-guard', 'Loop check', 'module', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
// Post bracket as the real catalogue ships it: the post-core EARLY gate first,
// then the parallel aspects, then the single FINAL decision. The two
// orchestrator rows carry the distinct catalogue display names.
const post = [
  step('post-orchestrator-review', 'Post-Core Orchestrator-Review', 'orchestrator', 'sequential'),
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('aspect-code-quality', 'Code quality', 'aspect', 'parallel'),
  step('aspect-tests-and-evidence', 'Tests and evidence', 'aspect', 'parallel'),
  step('post-lint-scss', 'Frontend stylelint', 'tool', 'sequential'),
  step('post-orchestrator-decision', 'Final Orchestrator-Review', 'orchestrator', 'sequential'),
  step('post-drift-adr-code', 'Drift: ADR / Code', 'drift', 'sequential'),
];
const allSteps = [...pre, ...core, ...post];

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
    startedAt: status === 'pending' ? null : '2026-06-05T08:00:00Z',
    completedAt: status === 'running' || status === 'pending' ? null : '2026-06-05T08:00:02Z',
    ...extra,
  };
}

function pipelineBody() {
  return {
    pipeline: {
      id: 'standard-task-pipeline',
      displayName: 'Standard task pipeline',
      version: 1,
      pre,
      core,
      post,
      allSteps,
    },
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-05T08:00:00Z',
      completedAt: '2026-06-05T08:00:06Z',
      steps: [
        execStep('pre-loop-guard', 'module', 'passed'),
        execStep('core-agent-run', 'core', 'passed'),
        // Early gate: its OWN result — clean completeness check, NOT a final verdict.
        execStep('post-orchestrator-review', 'orchestrator', 'passed', {
          verdict: 'complete',
          verdictSummary: 'Post-core completeness check: no unfinished evidence in the close-out.',
        }),
        execStep('aspect-requirement-fit', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('aspect-code-quality', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('aspect-tests-and-evidence', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('post-lint-scss', 'tool', 'passed'),
        // The SINGLE final verdict.
        execStep('post-orchestrator-decision', 'orchestrator', 'passed', {
          verdict: 'accept',
          verdictSummary: 'All aspect reviews passed; accepting and moving to human review.',
        }),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

// Accepted completion-loop terminal — pre-fix this rendered a redundant top
// "Note: Auto-review accepted … Moved to 5-human-review" banner.
function timelineBody() {
  return [
    {
      ts: '2026-06-05T08:00:06Z',
      kind: 'orchestrator_verdict_accepted',
      actor: 'orchestrator',
      summary: 'Auto-review accepted "Orchestrator-review distinct fixture" as done. Moved to 5-human-review for your approval.',
    },
  ];
}

async function installRoutes(page: Page, state: string) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }),
  );
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'auto', activeJobId: JOB_ID, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/timeline(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(timelineBody()) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(pipelineBody()) }),
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

test.describe('Pipeline: orchestrator-review rows are distinct, single final verdict', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
      } catch { /* private mode */ }
    });
  });

  test('one final-verdict chip, distinct names, grouped phases, no redundant accepted Note', async ({ page }) => {
    await installRoutes(page, '4-auto-review');
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // EXACTLY one "Final verdict" chip across the whole pipeline.
    const finalChips = page.getByTestId('overview-pipeline-step-final-verdict');
    await expect(finalChips).toHaveCount(1);

    // The post-core review row exists but is NOT the final verdict.
    const reviewRow = page.locator('[data-step-id="post-orchestrator-review"]');
    const decisionRow = page.locator('[data-step-id="post-orchestrator-decision"]');
    await expect(reviewRow).toHaveCount(1);
    await expect(decisionRow).toHaveCount(1);
    await expect(reviewRow).not.toHaveClass(/ov-pl-step--final-verdict/);
    await expect(decisionRow).toHaveClass(/ov-pl-step--final-verdict/);
    await expect(reviewRow.getByTestId('overview-pipeline-step-final-verdict')).toHaveCount(0);
    await expect(decisionRow.getByTestId('overview-pipeline-step-final-verdict')).toHaveCount(1);

    // Distinct names.
    await expect(reviewRow.getByTestId('overview-pipeline-step-name')).toHaveText('Post-Core Orchestrator-Review');
    await expect(decisionRow.getByTestId('overview-pipeline-step-name')).toHaveText('Final Orchestrator-Review');

    // Phase headers group the flat pipeline list without changing row order.
    const phaseHeaders = page.getByTestId('overview-pipeline-phase');
    await expect(phaseHeaders).toHaveText([
      /PRE\s+Preparation checks before the agent gets the task\./,
      /CORE\s+The coding agent run\./,
      /ASPECT\s+Parallel review passes over the finished work\./,
      /TOOL\s+Deterministic post-run tooling and evidence steps\./,
      /DECISION\s+The orchestrator ruling that accepts, reissues, or escalates\./,
      /DRIFT\s+Optional drift-analysis passes\./,
    ]);
    await expect(page.locator('[data-testid="overview-pipeline-phase"][data-phase="decision"]')).toHaveCount(1);

    // The accepted completion-loop strip shows the verdict but no redundant Note.
    await expect(page.getByTestId('overview-loop-verdict')).toHaveAttribute('data-verdict', 'accepted');
    await expect(page.getByTestId('overview-loop-reason')).toHaveCount(0);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-orchestrator-review-distinct-after.png'),
        fullPage: true,
      });
    }
  });
});
