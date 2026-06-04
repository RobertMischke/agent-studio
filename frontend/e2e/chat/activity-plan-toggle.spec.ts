import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Task-detail Activity tab: the Plan / Conversation / Trace sub-view toggle
 * (ASS-677..682). The Activity panel used to hang the task-plan strip above a
 * [Conversation] [Trace] switch. It is now a single segmented pill toggle:
 *
 *   - [Plan] appears only when the agent has emitted a usable plan, and is the
 *     default sub-view when present.
 *   - [Conversation] appears only when `Frontend:NextGenChat` is on.
 *   - [Trace] is always present (the legacy activity-log view).
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

async function pinEvidence(page: Page, jobId: string, planPresent: boolean): Promise<void> {
  const esc = encodeURIComponent(jobId);
  await page.route(`**/api/tasks/${esc}/output**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(buildOutputBuffer()) });
  });
  await page.route(`**/api/tasks/${esc}/plan**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(buildPlan(planPresent)) });
  });
}

async function setFlag(page: Page, on: boolean): Promise<void> {
  await page.addInitScript((enable) => {
    if (enable) localStorage.setItem('atp.flag.nextGenChat', '1');
    else localStorage.removeItem('atp.flag.nextGenChat');
  }, on);
}

async function pickJob(page: Page): Promise<{ id: string; watchPath: string } | null> {
  const res = await page.request.get('/api/tasks?includeFixtures=true');
  if (!res.ok()) return null;
  const jobs = (await res.json()) as { id: string; watchPath: string }[];
  if (!Array.isArray(jobs) || jobs.length === 0) return null;
  return { id: jobs[0].id, watchPath: jobs[0].watchPath };
}

async function openActivity(page: Page, job: { id: string; watchPath: string }): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 15_000 });
  await activityTab.click();
}

test.describe('Activity tab Plan / Conversation / Trace toggle', () => {
  test('plan present (flag off): Plan tab is default-active, toggle is [Plan] [Trace]', async ({ page }) => {
    const job = await pickJob(page);
    if (!job) { test.skip(true, 'No jobs on the board.'); return; }
    await setFlag(page, false);
    await pinEvidence(page, job.id, true);
    await openActivity(page, job);

    const planTab = page.getByTestId('activity-view-tab-plan');
    const traceTab = page.getByTestId('activity-view-tab-trace');
    await expect(planTab).toBeVisible({ timeout: 15_000 });
    await expect(traceTab).toBeVisible();
    // Flag off -> no Conversation tab.
    await expect(page.getByTestId('activity-view-tab-conversation')).toHaveCount(0);
    // Plan is the default sub-view when a plan exists.
    await expect(planTab).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('plan-strip')).toBeVisible();
    await expect(page.getByTestId('plan-item').first()).toBeVisible();

    await page.screenshot({ path: path.join(SHOTS_DIR, 'plan-default-flag-off.png'), fullPage: false });
  });

  test('Trace switch hides the plan; Plan switch brings it back', async ({ page }) => {
    const job = await pickJob(page);
    if (!job) { test.skip(true, 'No jobs on the board.'); return; }
    await setFlag(page, false);
    await pinEvidence(page, job.id, true);
    await openActivity(page, job);

    await expect(page.getByTestId('plan-strip')).toBeVisible({ timeout: 15_000 });

    // Switch to Trace: the legacy activity-log body shows, the plan hides.
    await page.getByTestId('activity-view-tab-trace').click();
    await expect(page.getByTestId('activity-log-body')).toBeVisible();
    await expect(page.getByTestId('plan-strip')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveAttribute('aria-selected', 'true');

    await page.screenshot({ path: path.join(SHOTS_DIR, 'trace-after-switch.png'), fullPage: false });

    // Back to Plan.
    await page.getByTestId('activity-view-tab-plan').click();
    await expect(page.getByTestId('plan-strip')).toBeVisible();
    await expect(page.getByTestId('activity-log-body')).toHaveCount(0);
  });

  test('no plan (flag off): no Plan tab, Trace is the body and the toggle is hidden', async ({ page }) => {
    const job = await pickJob(page);
    if (!job) { test.skip(true, 'No jobs on the board.'); return; }
    await setFlag(page, false);
    await pinEvidence(page, job.id, false);
    await openActivity(page, job);

    // Trace body renders directly; with only one sub-view the toggle is hidden.
    await expect(page.getByTestId('activity-log-body')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('activity-view-tab-plan')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    await expect(page.getByTestId('plan-strip')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'no-plan-trace-only.png'), fullPage: false });
  });

  test('plan present (flag on): toggle is [Plan] [Conversation] [Trace], Plan default-active', async ({ page }) => {
    const job = await pickJob(page);
    if (!job) { test.skip(true, 'No jobs on the board.'); return; }
    await setFlag(page, true);
    await pinEvidence(page, job.id, true);
    await openActivity(page, job);

    await expect(page.getByTestId('activity-view-tab-plan')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('activity-view-tab-conversation')).toBeVisible();
    await expect(page.getByTestId('activity-view-tab-trace')).toBeVisible();
    await expect(page.getByTestId('activity-view-tab-plan')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('plan-strip')).toBeVisible();

    // Conversation tab swaps in the next-gen renderer.
    await page.getByTestId('activity-view-tab-conversation').click();
    await expect(page.getByTestId('conversation-view')).toBeVisible();
    await expect(page.getByTestId('plan-strip')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'plan-default-flag-on.png'), fullPage: false });
  });
});
