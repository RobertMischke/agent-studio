import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Pipeline column headers (Time / Duration / Tokens / Cost).
 *
 * The per-step metric cells (start clock, duration, token count, cost) used to
 * render bare numbers with no captions. This polish introduces one header row
 * at the top of the steps so the columns are interpretable. The header cells
 * must line up over their value cells: each header cell's right edge sits on the
 * same x as the corresponding value cell's right edge (right-aligned columns).
 *
 * Fully mocked - no backend or git repository needed.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-column-headers';
const JOB_ID = 'pipeline-column-headers-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Column headers fixture',
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

const pre = [step('pre-loop-guard', 'Loop guard', 'pre', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential')];
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

function execStep(stepId: string, kind: string, model: string, extra: Record<string, unknown> = {}) {
  return {
    stepId,
    kind,
    status: 'passed',
    model,
    durationMs: 92_000,
    inputTokens: 8_000,
    outputTokens: 2_000,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: '2026-06-02T08:00:00Z',
    completedAt: '2026-06-02T08:01:32Z',
    ...extra,
  };
}

function costStep(stepId: string, model: string, totalTokens: number, costUsd: number) {
  return {
    stepId,
    model,
    modelKnown: true,
    tokenUsageSource: 'orchestrator',
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

function pipelineWithMetrics() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: '2026-06-02T08:05:00Z',
      steps: [
        execStep('pre-loop-guard', 'pre', 'claude-haiku-4-5'),
        execStep('core-agent-run', 'core', 'claude-opus-4-7'),
        execStep('post-orchestrator-decision', 'orchestrator', 'claude-haiku-4-5', { verdict: 'accept' }),
      ],
    },
    cost: {
      steps: [
        costStep('pre-loop-guard', 'claude-haiku-4-5', 1_200, 0.0021),
        costStep('core-agent-run', 'claude-opus-4-7', 248_000, 4.37),
        costStep('post-orchestrator-decision', 'claude-haiku-4-5', 5_400, 0.0089),
      ],
      totalInputTokens: 203_680,
      totalOutputTokens: 50_920,
      totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0,
      totalTokens: 254_600,
      totalInputCostUsd: 2.6286,
      totalOutputCostUsd: 1.7524,
      totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0,
      totalCostUsd: 4.3810,
      anyModelUnknown: false,
    },
    config: {},
  };
}

async function installRoutes(page: Page, state: string, pipelineBody: () => unknown) {
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

test.describe('Pipeline: per-step metric column headers', () => {
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

  test('a header row labels the Time / Duration / Tokens / Cost columns, right-aligned over their values', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineWithMetrics);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // The header appears once and carries all four captions.
    const header = page.getByTestId('overview-pipeline-header');
    await expect(header).toHaveCount(1);
    await expect(header.locator('.ov-pl-header__time')).toHaveText('Time');
    await expect(header.locator('.ov-pl-header__duration')).toHaveText('Duration');
    await expect(header.locator('.ov-pl-header__tokens')).toHaveText('Tokens');
    await expect(header.locator('.ov-pl-header__cost')).toHaveText('Cost');

    // Right-edge alignment: each header cell sits over its value cell. Measured
    // against the core row, which has populated timing, tokens and cost. Wait
    // for the poll to populate the row content before measuring.
    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    await expect(coreRow.locator('.ov-pl-step__tokens')).toHaveText('248.0k');

    const align = await page.evaluate(() => {
      const right = (sel: string, root: ParentNode = document): number => {
        const el = root.querySelector<HTMLElement>(sel);
        if (!el) throw new Error(`missing ${sel}`);
        return el.getBoundingClientRect().right;
      };
      const coreRow = document.querySelector<HTMLElement>('[data-step-id="core-agent-run"]')!;
      return {
        time: { h: right('.ov-pl-header__time'), v: right('.ov-pl-step__started', coreRow) },
        duration: { h: right('.ov-pl-header__duration'), v: right('.ov-pl-step__duration', coreRow) },
        tokens: { h: right('.ov-pl-header__tokens'), v: right('.ov-pl-step__tokens', coreRow) },
        cost: { h: right('.ov-pl-header__cost'), v: right('.ov-pl-step__cost', coreRow) },
      };
    });

    for (const [col, { h, v }] of Object.entries(align)) {
      expect(Math.abs(h - v), `${col}: header right ${h} vs value right ${v} (${JSON.stringify(align)})`).toBeLessThanOrEqual(1.5);
    }

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-column-headers.png'),
        fullPage: true,
      });
    }
  });

  test('overview content keeps left-aligned prose and tabular measures on ultrawide viewports', async ({ page }) => {
    await page.setViewportSize({ width: 1900, height: 1050 });
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: true, git: false }),
        );
      } catch {
        /* private mode */
      }
    });

    await installRoutes(page, '4-auto-review', pipelineWithMetrics);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const overview = page.getByTestId('overview-tab');
    const title = page.getByTestId('overview-title-block');
    const status = page.getByTestId('overview-status');
    const pipeline = page.getByTestId('overview-pipeline');
    const protocol = page.getByTestId('pane-protocol');

    await expect(overview).toBeVisible({ timeout: 10_000 });
    await expect(protocol).toBeVisible();
    await expect(pipeline).toBeVisible();

    const geometry = await page.evaluate(() => {
      const box = (selector: string): DOMRect => {
        const el = document.querySelector<HTMLElement>(selector);
        if (!el) throw new Error(`missing ${selector}`);
        return el.getBoundingClientRect();
      };

      const overview = box('[data-testid="overview-tab"]');
      const title = box('[data-testid="overview-title-block"]');
      const status = box('[data-testid="overview-status"]');
      const pipeline = box('[data-testid="overview-pipeline"]');
      const protocol = box('[data-testid="pane-protocol"]');
      const coreRow = box('[data-step-id="core-agent-run"]');
      const coreCost = box('[data-step-id="core-agent-run"] .ov-pl-step__cost');
      const titleStyle = getComputedStyle(document.querySelector<HTMLElement>('[data-testid="overview-title-block"]')!);
      const pipelineStyle = getComputedStyle(document.querySelector<HTMLElement>('[data-testid="overview-pipeline"]')!);

      return {
        overviewLeft: overview.left,
        titleLeft: title.left,
        titleWidth: title.width,
        statusLeft: status.left,
        statusWidth: status.width,
        pipelineLeft: pipeline.left,
        pipelineWidth: pipeline.width,
        protocolLeft: protocol.left,
        protocolWidth: protocol.width,
        coreRowRight: coreRow.right,
        coreCostRight: coreCost.right,
        proseMax: titleStyle.maxWidth,
        pipelineMax: pipelineStyle.maxWidth,
      };
    });

    // The central measure primitive is explicitly left-aligned, not centered
    // inside the prompt pane.
    expect(Math.abs(geometry.titleLeft - geometry.overviewLeft)).toBeLessThanOrEqual(20);
    expect(Math.abs(geometry.statusLeft - geometry.overviewLeft)).toBeLessThanOrEqual(20);
    expect(Math.abs(geometry.pipelineLeft - geometry.overviewLeft)).toBeLessThanOrEqual(20);

    // Prose stays on a character measure; tabular blocks get the wider but
    // still capped pixel measure from the shared tokens.
    expect(geometry.proseMax).toBe('74ch');
    expect(geometry.pipelineMax).toBe('900px');
    expect(geometry.titleWidth).toBeLessThan(900);
    expect(geometry.statusWidth).toBeLessThanOrEqual(1040);
    expect(geometry.pipelineWidth).toBeLessThanOrEqual(940);

    // Pipeline metrics remain near the row instead of drifting toward the
    // viewport edge, and the right protocol/activity pane keeps its own space.
    expect(geometry.coreCostRight).toBeLessThanOrEqual(geometry.coreRowRight + 1);
    expect(geometry.coreRowRight).toBeLessThan(geometry.protocolLeft);
    expect(geometry.protocolWidth).toBeGreaterThan(260);

    if (RESULTS_DIR) {
      await overview.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'overview-content-measures-wide.png'),
        fullPage: true,
      });
    }
  });
});
