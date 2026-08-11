import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Task-detail Activity tab: one living agent-plan checklist in the readable
 * event stream. Raw TODO_LIST frames remain exclusive to Trace:
 *
 *   - The plan and agent events are visible together, without a Plan tab.
 *   - Trace hides the derived checklist and shows raw frames.
 *   - Agent events, Debug, and Copy live in the Activity overflow menu.
 *
 * The spec drives the live frontend (proxied to a real backend) but pins the
 * two pieces of evidence the behaviour keys off: the per-job `output` buffer
 * (so the panel is in its "has output" branch) and the per-job `plan` (so the
 * Plan tab is deterministically present or absent). Everything else (detail,
 * runs, board) is served by the backend the frontend proxies to.
 *
 * Screenshots land under JOB_RESULTS_DIR/plan-toggle when the orchestrator
 * sets it, else test-results/ (scratch).
 */

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  || process.env.PLAN_TOGGLE_SHOTS?.trim()
  || path.resolve(__dirname, '../../test-results/plan-toggle');

const TARGET = { id: 'activity-toolbar-fixture', watchPath: 'C:/fixtures/activity-toolbar' };

interface OutLine { timestamp: string; stream: string; text: string; }

function buildOutputBuffer(): OutLine[] {
  const t0 = Date.now() - 6 * 60 * 1000;
  const t = (s: number) => new Date(t0 + s * 1000).toISOString();
  return [
    { timestamp: t(0), stream: 'user', text: 'Show the current agent plan in Activity.' },
    { timestamp: t(1), stream: 'stdout', text: '{"type":"item.started","item":{"id":"item_1","type":"todo_list","items":[{"text":"Inspect TODO_LIST frames","completed":false},{"text":"Render one living checklist","completed":false},{"text":"Verify both themes","completed":false}]}}' },
    { timestamp: t(3), stream: 'stdout', text: '* Read protocol-pane.component.html' },
    { timestamp: t(4), stream: 'stdout', text: '{"type":"item.updated","item":{"id":"item_1","type":"todo_list","items":[{"text":"Inspect TODO_LIST frames","completed":true},{"text":"Render one living checklist","completed":false},{"text":"Verify both themes","completed":false}]}}' },
    { timestamp: t(5), stream: 'stdout', text: '* Edit protocol-pane.component.ts' },
    { timestamp: t(8), stream: 'stdout', text: 'The checklist now updates in place.' },
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
    source: 'codex/todo_list',
    snapshotCount: 3,
    activeItemId: 'i2',
    softEstimateMedian: 2,
    items: [
      { id: 'i1', title: 'Inspect TODO_LIST frames', status: 'done', subActionCount: 2, subActions: [{ ts, tool: 'Read', label: 'Captured Codex frames' }] },
      { id: 'i2', title: 'Render one living checklist', status: 'active', subActionCount: 1, subActions: [{ ts, tool: 'Edit', label: 'protocol-pane.component.html' }] },
      { id: 'i3', title: 'Verify both themes', status: 'pending', subActionCount: 0, subActions: [] },
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
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: false }),
    }));
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
  await page.route('**/api/projects/fixture/workbenches**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projectName: 'fixture', includesHistory: true, count: 0, items: [] }),
    }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
    }));
  await page.route('**/api/runner/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projects: { fixture: { projectName: 'fixture', mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }),
    }));
  await page.route(`**/api/tasks/${esc}/output**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: output }));
  await page.route(`**/api/tasks/${esc}/plan**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: plan }));
  await page.route(`**/api/tasks/${esc}/pipeline**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ pipeline: { pre: [], core: [], post: [], allSteps: [] }, execution: null, executions: [], config: {}, cost: null }),
    }));
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
  // `nextGenChat` is default-ON now: a missing key reads as opt-in, so the
  // off-state must be written explicitly as '0' (mirrors writeExplicit in
  // FeatureFlagsService) rather than removing the key.
  await page.addInitScript((enable) => {
    localStorage.setItem('atp.flag.nextGenChat', enable ? '1' : '0');
    localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
  }, on);
}

async function openActivity(page: Page, job: { id: string; watchPath: string }): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
  await expect(page).toHaveURL(/#\/tasks\//, { timeout: 20_000 });
  await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 20_000 });
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 20_000 });
  await activityTab.click();
}

test.describe('Activity live agent plan', () => {
  test('plan and legacy agent events share one stream in both themes', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    await expect(page.getByTestId('activity-view-tab-plan')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-cli')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    const activity = page.getByTestId('activity-panel');
    await expect(activity.getByTestId('plan-strip')).toBeVisible({ timeout: 15_000 });
    await expect(activity.getByTestId('activity-log-body')).toBeVisible();
    await expect(activity.getByTestId('plan-item').first()).toBeVisible();
    await expect(activity.getByTestId('plan-item-status')).toHaveText(['Done', 'Active', 'Open']);
    await expect(activity.getByTestId('activity-log-body')).not.toContainText('"type":"item.updated"');

    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate((value) => {
        document.documentElement.dataset['studioTheme'] = value;
        localStorage.setItem('atp.studio.theme', value);
      }, theme);
      await page.screenshot({ path: path.join(SHOTS_DIR, `activity-todo-checklist-${theme}--mocked.png`), fullPage: false });
    }
  });

  test('Trace shows raw frames and Agent events returns to the living checklist', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    const activity = page.getByTestId('activity-panel');
    await expect(activity.getByTestId('plan-strip')).toBeVisible({ timeout: 15_000 });

    await page.getByTestId('activity-toolbar-menu').click();
    const traceItem = page.getByTestId('activity-toolbar-menu-item-trace');
    await expect(traceItem).toBeVisible();
    await expect(page.getByTestId('activity-toolbar-menu-item-debug')).toContainText('Debug');
    await expect(page.getByTestId('activity-toolbar-menu-item-conversation')).toContainText('Agent events');
    await expect(page.getByTestId('activity-toolbar-menu-item-copy')).toContainText('Copy');
    await page.screenshot({ path: path.join(SHOTS_DIR, 'toolbar-menu-open--mocked.png'), fullPage: false });
    await traceItem.click();
    await expect(activity.getByTestId('activity-log-body')).toBeVisible();
    await expect(activity.getByTestId('activity-log-trace')).toBeVisible();
    await expect(activity.getByTestId('plan-strip')).toHaveCount(0);
    await expect(activity.getByTestId('activity-log-body')).toContainText('item.updated');

    await page.screenshot({ path: path.join(SHOTS_DIR, 'trace-after-switch--mocked.png'), fullPage: false });

    await page.getByTestId('activity-toolbar-menu').click();
    await page.getByTestId('activity-toolbar-menu-item-conversation').click();
    await expect(activity.getByTestId('plan-strip')).toBeVisible();
    await expect(activity.getByTestId('activity-log-conversation')).toBeVisible();
  });

  test('no plan (flag off): event body renders without an empty single-tab toggle', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, false);
    await openActivity(page, TARGET);

    await expect(page.getByTestId('activity-view-tab-cli')).toHaveCount(0);
    const activity = page.getByTestId('activity-panel');
    await expect(activity.getByTestId('activity-log-body')).toBeVisible({ timeout: 15_000 });
    await expect(activity.getByTestId('activity-log-conversation')).toBeVisible();
    await expect(page.getByTestId('activity-view-tab-plan')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    await expect(activity.getByTestId('plan-strip')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'no-plan-events--mocked.png'), fullPage: false });
  });

  test('next-generation agent events render below the same checklist', async ({ page }) => {
    await setFlag(page, true);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    await expect(page.getByTestId('activity-view-tab-cli')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    await page.getByTestId('activity-toolbar-menu').click();
    const eventsItem = page.getByTestId('activity-toolbar-menu-item-conversation');
    await expect(eventsItem).toBeVisible();
    await expect(eventsItem).toContainText('Agent events');
    await eventsItem.click();
    const activity = page.getByTestId('activity-panel');
    await expect(activity.getByTestId('conversation-view')).toBeVisible({ timeout: 15_000 });
    await expect(activity.getByTestId('plan-strip')).toBeVisible();
  });
});
