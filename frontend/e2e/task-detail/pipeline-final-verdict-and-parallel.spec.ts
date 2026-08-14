import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Auto-review display: parallel aspects + the orchestrator final verdict as
 * separate pipeline steps.
 *
 * Acceptance ("die parallelen Aspekte UND das Orchestrator-Final-Verdict als
 * eigene, klar getrennte Schritte im Job-Details darstellen"): aspect reviews
 * run in a read-only parallel pool, so each aspect row carries a muted
 * parallel note. The orchestrator then makes ONE final ruling, recorded as its own
 * `post-orchestrator-decision` step (kind `orchestrator`) and rendered as a
 * visually separated final-verdict row with a "Final verdict" chip.
 *
 * Fully mocked - no backend or git repository needed; the pipeline block is
 * generic and renders one row per step from the joined catalogue + execution.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-final-verdict';
const JOB_ID = 'pipeline-final-verdict-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Final verdict fixture',
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

// Core run (sequential), then the read-only aspect pool (parallel), then the
// single orchestrator final verdict (sequential).
const pre = [step('pre-loop-guard', 'Loop guard', 'pre', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [
  step('analysis-qs-angular-rules', 'QS Angular rule analysis', 'analysis', 'sequential'),
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('aspect-code-quality', 'Code quality', 'aspect', 'parallel'),
  step('aspect-tests-and-evidence', 'Tests and evidence', 'aspect', 'parallel'),
  step('post-lint-scss', 'Frontend stylelint', 'tool', 'sequential'),
  step('post-regression-radar', 'Regression radar', 'drift', 'sequential'),
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

// All aspects passed in the parallel pool, so the orchestrator's single final
// verdict is `accept`.
function pipelineAcceptedFinalVerdict() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: '2026-06-02T08:00:04Z',
      steps: [
        execStep('pre-loop-guard', 'pre', 'passed'),
        execStep('core-agent-run', 'core', 'passed'),
        execStep('analysis-qs-angular-rules', 'analysis', 'passed', { verdict: 'pass' }),
        execStep('aspect-requirement-fit', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('aspect-code-quality', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('aspect-tests-and-evidence', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('post-lint-scss', 'tool', 'passed'),
        execStep('post-regression-radar', 'drift', 'passed', { verdict: 'clean' }),
        execStep('post-orchestrator-decision', 'orchestrator', 'passed', {
          verdict: 'accept',
          verdictSummary:
            'All 3 aspect reviews passed in the read-only pool; accepting and moving to human review.',
        }),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
    resultFiles: { 'aspect-requirement-fit': 'aspect-requirement-fit.md' },
  };
}

async function installRoutes(page: Page, state: string, pipelineBody: () => unknown) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }),
  );
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
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/(status\\.md|aspect-[^?]+\\.md)(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/plain',
      body: [
        '---',
        'status: pass',
        '---',
        '',
        '## Model reply',
        '',
        '```',
        '## Requirement Fit Review',
        '',
        'The implementation matches the task prompt and no blocking gap remains.',
        '```',
        '[[ASPECT_VERDICT: status=pass]]',
      ].join('\n'),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
  await page.route(/\/api\/projects\/[^/]+\/workbenches(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projectName: PROJECT, includesHistory: true, count: 0, items: [] }),
    }),
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
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => undefined);
  }
}

/**
 * Expand every collapsible pipeline section (ASS-1914). Sections that hold no
 * running/failed work default-collapse, so a test that measures the full
 * configured row set (e.g. the shared stage gutter across PRE…DRIFT) must open
 * every section first. Each header is a toggle button carrying `aria-expanded`.
 */
async function expandAllPipelineSections(page: Page): Promise<void> {
  for (let i = 0; i < 20; i++) {
    const expanded = await page.evaluate(() => {
      const phase = document.querySelector<HTMLButtonElement>(
        '[data-testid="overview-pipeline-phase"][aria-expanded="false"]',
      );
      phase?.click();
      return phase !== null;
    });
    if (!expanded) break;
  }
}

test.describe('Pipeline: parallel aspects + orchestrator final verdict', () => {
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

  test('aspect rows show muted parallel metadata and the orchestrator decision is a separate final-verdict step', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineAcceptedFinalVerdict);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    const analysisRow = page.locator('[data-step-id="analysis-qs-angular-rules"]');
    await expect(analysisRow).toBeVisible();
    await expect(analysisRow).toContainText('QS Angular rule analysis');
    await expect(pipeline.getByText('ANALYSIS', { exact: true })).toBeVisible();

    // Each aspect row carries quiet parallel metadata (read-only pool, Req 1 + 3).
    const parallelNotes = page.getByTestId('overview-pipeline-step-parallel');
    await expect(parallelNotes).toHaveCount(3);
    await expect(parallelNotes.first()).toHaveText('∥');
    await expect(parallelNotes.first()).toHaveAttribute('aria-label', 'Parallel review pool');

    // The orchestrator decision is its own, clearly separated final-verdict row.
    // Its compact icon marker keeps the full kind available to assistive tech.
    const decisionRow = page.locator('[data-step-id="post-orchestrator-decision"]');
    await expect(decisionRow).toBeVisible();
    await expect(decisionRow.locator('.ov-pl-step__kind')).toHaveAttribute('aria-label', 'Decision step');
    await expect(decisionRow).toHaveClass(/ov-pl-step--final-verdict/);
    await expect(decisionRow).toHaveAttribute('data-run-mode', 'sequential');

    const finalChip = decisionRow.getByTestId('overview-pipeline-step-final-verdict');
    await expect(finalChip).toBeVisible();
    await expect(finalChip).toContainText('Final verdict');

    // The one combined chip projects the authoritative current-run outcome.
    const verdict = decisionRow.getByTestId('overview-pipeline-step-final-verdict');
    await expect(verdict).toHaveAttribute('data-verdict', 'succeeded');
    await expect(verdict).toContainText('Final verdict → Pipeline completed');

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-parallel-aspects-and-final-verdict.png'),
        fullPage: true,
      });
    }

    const aspectRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    await aspectRow.getByTestId('overview-pipeline-step-details').click();
    const detailsDialog = page.getByTestId('overview-pipeline-step-details-dialog');
    await expect(detailsDialog).toBeVisible();
    const resultTrigger = detailsDialog.getByTestId('pipeline-step-result-toggle');
    await expect(resultTrigger).toBeVisible();
    await resultTrigger.click();
    await expect(detailsDialog.getByTestId('pipeline-step-result-card')).toBeVisible();
    await expect(detailsDialog.getByTestId('pipeline-step-result-body')).toContainText('Requirement Fit Review');

    const popoverBackground = await detailsDialog
      .getByTestId('pipeline-step-result-card')
      .evaluate((el) => getComputedStyle(el).backgroundColor);
    const channels = popoverBackground.match(/rgba?\(([^)]+)\)/);
    const parts = channels?.[1].split(',').map((part) => part.trim()) ?? [];
    const popoverAlpha = parts.length === 4 ? Number(parts[3]) : 1;
    expect(
      popoverAlpha,
      `aspect popover background must be opaque, got "${popoverBackground}"`,
    ).toBe(1);

    if (RESULTS_DIR) {
      await detailsDialog.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-result-popover-open.png'),
      });
    }
  });

  test('stage labels use one fixed gutter and names start on one shared edge', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineAcceptedFinalVerdict);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    const metrics = await page.getByTestId('overview-pipeline-step').evaluateAll((rows) =>
      rows.map((row) => {
        const kind = row.querySelector<HTMLElement>('.ov-pl-step__kind');
        const name = row.querySelector<HTMLElement>('.ov-pl-step__name');
        if (!kind || !name) throw new Error('Pipeline row is missing kind or name cell.');
        const kindRect = kind.getBoundingClientRect();
        const nameRect = name.getBoundingClientRect();
        return {
          label: kind.getAttribute('aria-label') ?? '',
          kindLeft: Math.round(kindRect.left),
          kindWidth: Math.round(kindRect.width),
          nameLeft: Math.round(nameRect.left),
        };
      }),
    );

    expect(metrics.map(m => m.label)).toEqual([
      'pre step',
      'Core agent work step',
      'Analysis step',
      'Aspect step',
      'Aspect step',
      'Aspect step',
      'Tool step',
      'Drift step',
      'Decision step',
    ]);

    const maxDelta = (values: number[]) => Math.max(...values) - Math.min(...values);
    expect(maxDelta(metrics.map(m => m.kindLeft)), JSON.stringify(metrics)).toBeLessThanOrEqual(1);
    expect(maxDelta(metrics.map(m => m.kindWidth)), JSON.stringify(metrics)).toBeLessThanOrEqual(1);
    expect(maxDelta(metrics.map(m => m.nameLeft)), JSON.stringify(metrics)).toBeLessThanOrEqual(1);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-fixed-stage-gutter-alignment.png'),
        fullPage: true,
      });
    }
  });
});
