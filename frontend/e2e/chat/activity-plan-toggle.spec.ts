import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Task-detail Activity tab: the Plan / CLI sub-view toggle
 * (ASS-677..682). The Activity panel used to hang the task-plan strip above a
 * [Conversation] [Trace] switch. It is now a single segmented pill toggle:
 *
 *   - [Plan] appears only when the agent has emitted a usable plan, and is the
 *     default sub-view when present.
 *   - [CLI] is always present and renders the CLI conversation/output view.
 *   - [Trace], [Debug], and [Copy] live in the Activity overflow menu.
 *
 * The spec drives the live frontend (proxied to a real backend) but pins the
 * two pieces of evidence the behaviour keys off — the per-job `output` buffer
 * (so the panel is in its "has output" branch) and the per-job `plan` (so the
 * Plan tab is deterministically present or absent). Everything else (detail,
 * runs, board) is served by the backend the frontend proxies to.
 *
 * Screenshots land under JOB_RESULTS_DIR/plan-toggle when the orchestrator
 * sets it, else test-results/ (scratch).
 */

const SHOTS_DIR = process.env.PLAN_TOGGLE_SHOTS?.trim()
  || path.resolve(__dirname, '../../test-results/plan-toggle');

const TARGET = { id: 'activity-toolbar-fixture', watchPath: 'C:/fixtures/activity-toolbar' };

interface OutLine { timestamp: string; stream: string; text: string; }

function buildOutputBuffer(): OutLine[] {
  const t0 = Date.now() - 6 * 60 * 1000;
  const t = (s: number) => new Date(t0 + s * 1000).toISOString();
  return [
    { timestamp: t(0), stream: 'user', text: 'Restructure the Activity tab so the plan is its own panel.' },
    { timestamp: t(3), stream: 'stdout', text: '* Read protocol-pane.component.html' },
    { timestamp: t(5), stream: 'stdout', text: '* Edit protocol-pane.component.ts' },
    { timestamp: t(8), stream: 'stdout', text: 'Wired the [Plan] [Conversation] [Trace] toggle.' },
    { timestamp: t(10), stream: 'stdout', text: '[[TASK_DONE]]' },
  ];
}

function buildPlan(present: boolean) {
  if (!present) {
    return { hasPlan: false, source: null, snapshotCount: 0, activeItemId: null, softEstimateMedian: null, items: [], unassignedSubActions: [] };
  }
  const ts = new Date(Date.now() - 4 * 60 * 1000).toISOString();
  return {
    hasPlan: true,
    source: 'claude',
    snapshotCount: 3,
    activeItemId: 'i2',
    softEstimateMedian: 2,
    items: [
      { id: 'i1', title: 'Make the plan its own panel', status: 'done', subActionCount: 2, subActions: [{ ts, tool: 'Edit', label: 'protocol-pane.component.ts' }] },
      { id: 'i2', title: 'Default to Plan when a plan exists', status: 'active', subActionCount: 1, subActions: [{ ts, tool: 'Edit', label: 'protocol-pane.component.html' }] },
      { id: 'i3', title: 'Add the toggle spec', status: 'pending', subActionCount: 0, subActions: [] },
    ],
    unassignedSubActions: [],
  };
}

function detail() {
  return {
    info: {
      id: TARGET.id,
      taskKey: `ASS-E2E-${TARGET.id}`,
      displayKey: 'ASS-E2E',
      title: 'Activity toolbar fixture',
      state: '5-human-review',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5',
      watchPath: TARGET.watchPath,
      projectName: 'fixture',
      folderPath: `${TARGET.watchPath}/.orchestrator/jobs/5-human-review/${TARGET.id}`,
      createdAt: '2026-06-09T08:00:00.000Z',
      lastActivity: '2026-06-09T08:05:00.000Z',
      sessionName: null,
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      codeActivityDetected: false,
      summaryState: null,
      taskType: 'bug',
      tags: [],
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    },
    promptMarkdown: 'Fixture prompt.',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: '## Status\n\nWaiting for review.',
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page, planPresent: boolean): Promise<void> {
  const esc = encodeURIComponent(TARGET.id);
  const output = JSON.stringify(buildOutputBuffer());
  const plan = JSON.stringify(buildPlan(planPresent));
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        codeNotComplete: [],
        autoReview: [],
        humanReview: [detail().info],
        review: [],
        completed: [],
        archive: [],
      }),
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: 'fixture', path: TARGET.watchPath, rootPath: TARGET.watchPath }]),
    }));
  await page.route('**/api/runner/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projects: { fixture: { projectName: 'fixture', mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }),
    }));
  await page.route(`**/api/tasks/${esc}/output**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: output }));
  await page.route(`**/api/jobs/${esc}/output**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: output }));
  await page.route(`**/api/tasks/${esc}/plan**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: plan }));
  await page.route(`**/api/jobs/${esc}/plan**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: plan }));
  await page.route(`**/api/tasks/${esc}/runs**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(`**/api/tasks/${esc}/session-events**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(`**/api/tasks/${esc}/claude-session**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(`**/api/tasks/${esc}?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail()) }));
}

async function setFlag(page: Page, on: boolean): Promise<void> {
  await page.addInitScript((enable) => {
    if (enable) localStorage.setItem('atp.flag.nextGenChat', '1');
    else localStorage.removeItem('atp.flag.nextGenChat');
  }, on);
}

async function openActivity(page: Page, job: { id: string; watchPath: string }): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 20_000 });
  await activityTab.click();
}

test.describe('Activity tab Plan / CLI toggle', () => {
  test('plan present (flag off): Plan tab is default-active, toggle is [Plan] [CLI]', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    const planTab = page.getByTestId('activity-view-tab-plan');
    const cliTab = page.getByTestId('activity-view-tab-cli');
    await expect(planTab).toBeVisible({ timeout: 15_000 });
    await expect(cliTab).toBeVisible();
    // Trace is no longer a primary tab.
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    // The old Conversation test id was retired in favour of the user-facing CLI label.
    await expect(page.getByTestId('activity-view-tab-conversation')).toHaveCount(0);
    // Plan is the default sub-view when a plan exists.
    await expect(planTab).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('plan-strip')).toBeVisible();
    await expect(page.getByTestId('plan-item').first()).toBeVisible();

    await page.screenshot({ path: path.join(SHOTS_DIR, 'plan-default-flag-off.png'), fullPage: false });
  });

  test('Trace overflow action hides the plan; Plan switch brings it back', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    await expect(page.getByTestId('plan-strip')).toBeVisible({ timeout: 15_000 });

    // Switch to Trace from the overflow menu: the raw activity-log body shows, the plan hides.
    await page.getByTestId('activity-toolbar-menu').click();
    const traceItem = page.getByTestId('activity-toolbar-menu-item-trace');
    await expect(traceItem).toBeVisible();
    await expect(page.getByTestId('activity-toolbar-menu-item-debug')).toContainText('Debug');
    await expect(page.getByTestId('activity-toolbar-menu-item-copy')).toContainText('Copy');
    await page.screenshot({ path: path.join(SHOTS_DIR, 'toolbar-menu-open.png'), fullPage: false });
    await traceItem.click();
    await expect(page.getByTestId('activity-log-body')).toBeVisible();
    await expect(page.getByTestId('activity-log-trace')).toBeVisible();
    await expect(page.getByTestId('plan-strip')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-cli')).toBeVisible();

    await page.screenshot({ path: path.join(SHOTS_DIR, 'trace-after-switch.png'), fullPage: false });

    // Back to Plan.
    await page.getByTestId('activity-view-tab-plan').click();
    await expect(page.getByTestId('plan-strip')).toBeVisible();
    await expect(page.getByTestId('activity-log-body')).toHaveCount(0);
  });

  test('no plan (flag off): no Plan tab, CLI is the body and the toggle stays visible', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, false);
    await openActivity(page, TARGET);

    // CLI body renders directly; the single-tab toggle stays visible to anchor the overflow menu row.
    await expect(page.getByTestId('activity-view-tab-cli')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('activity-view-tab-cli')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('activity-log-body')).toBeVisible();
    await expect(page.getByTestId('activity-log-conversation')).toBeVisible();
    await expect(page.getByTestId('activity-view-tab-plan')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    await expect(page.getByTestId('plan-strip')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'no-plan-trace-only.png'), fullPage: false });
  });

  test('plan present (flag on): toggle is [Plan] [CLI], Plan default-active', async ({ page }) => {
    await setFlag(page, true);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    await expect(page.getByTestId('activity-view-tab-plan')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('activity-view-tab-cli')).toBeVisible();
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-plan')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('plan-strip')).toBeVisible();

    // CLI tab swaps in the next-gen renderer when the feature flag is on.
    await page.getByTestId('activity-view-tab-cli').click();
    await expect(page.getByTestId('conversation-view')).toBeVisible();
    await expect(page.getByTestId('plan-strip')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'plan-default-flag-on.png'), fullPage: false });
  });
});
