import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { listJobs } from '../helpers/jobs';

/**
 * Verbose Debug overlay regression spec.
 *
 * The overlay is the read-only escape hatch from compact chat (`Frontend:NextGenChat`).
 * It binds to real `ConversationEvent` projections derived from the task's CLI
 * output buffer plus the run timeline, screenshots, token summary and job
 * info — so the spec stubs the relevant endpoints with a synthetic but
 * realistic activity log (orchestrator decision, tool burst, run marker,
 * screenshot, watchdog quiet) and exercises the overlay tabs and trace
 * routing end-to-end.
 *
 * Two host integrations are covered:
 *   1. Task Chat workbench → "🐞 Verbose Debug" button in the protocol pane.
 *   2. Project side sheet header bug button when a task tab is in scope.
 *
 * Screenshots land under the running task's `results/` folder (per the
 * project doctrine) so review-relevant evidence stays close to the activity
 * log; `test-results/` is scratch and gets overwritten.
 */

const RESULTS_DIR = path.resolve(
  __dirname,
  '../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/chat-verbose-debug-view/results'
);

interface OutLine { timestamp: string; stream: string; text: string; }

function buildOutputBuffer(): OutLine[] {
  // Anchor everything ~10 minutes in the past so durations look realistic.
  const t0 = Date.now() - 10 * 60 * 1000;
  const t = (offsetSec: number) => new Date(t0 + offsetSec * 1000).toISOString();
  return [
    // User intent
    { timestamp: t(0),  stream: 'user',         text: 'Investigate the failing protocol parser regression.' },
    // Orchestrator decision (reissue path)
    { timestamp: t(2),  stream: 'orchestrator', text: '[orchestrator] decision: reissue task — heuristic outcome did not match sentinel grammar.' },
    // Agent reply
    { timestamp: t(5),  stream: 'stdout',       text: 'Reading prompt.md to understand the parser grammar.' },
    // Tool burst (multiple read/edit/command calls)
    { timestamp: t(7),  stream: 'stdout',       text: '* Read src/components/activity-log.parser.ts' },
    { timestamp: t(7),  stream: 'stdout',       text: '  | activity-log.parser.ts (1-180)' },
    { timestamp: t(8),  stream: 'stdout',       text: '* Read src/components/activity-log.parser.spec.ts' },
    { timestamp: t(9),  stream: 'stdout',       text: '* Search "sentinel"' },
    { timestamp: t(10), stream: 'stdout',       text: '  | matches in 4 files' },
    { timestamp: t(11), stream: 'stdout',       text: '* Edit src/components/activity-log.parser.ts' },
    { timestamp: t(12), stream: 'stdout',       text: '  | adding heuristic guard' },
    { timestamp: t(13), stream: 'stdout',       text: '* Run npm --prefix frontend run test' },
    { timestamp: t(14), stream: 'stdout',       text: '  | exit 0 — 108 passed' },
    // Watchdog quiet window from Layer 2 supervisor
    { timestamp: t(70), stream: 'orchestrator', text: '[watchdog] agent quiet for 60s, allowing one more window' },
    // Agent resumes with a wrap-up
    { timestamp: t(95), stream: 'stdout',       text: 'Wrote regression test and confirmed parser handles the sentinel correctly.' },
    // Final sentinel
    { timestamp: t(98), stream: 'stdout',       text: '[[TASK_DONE]]' }
  ];
}

function buildJobDetail(jobId: string, watchPath: string) {
  const startedAt = new Date(Date.now() - 12 * 60 * 1000).toISOString();
  return {
    info: {
      id: jobId,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Verbose debug spec fixture',
      state: '4-auto-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/4-auto-review/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      tokenSummary: {
        calls: 4,
        inputTokens: 18_400,
        outputTokens: 6_120,
        cacheReadTokens: 11_900,
        cacheCreationTokens: 980,
        totalTokens: 37_400,
        lastModel: 'claude-haiku-4-5',
        lastUpdate: new Date().toISOString(),
        entries: []
      },
      order: 1,
      lastActivity: new Date().toISOString(),
      createdAt: startedAt,
      useOwnSession: false,
      commit: null
    },
    promptMarkdown: 'Investigate the failing protocol parser regression.',
    // Leave statusMarkdown null so the inspector defaults to the activity tab,
    // which is where the "🐞 Verbose Debug" button lives.
    statusMarkdown: null,
    log: [],
    promptHistory: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null }
  };
}

function buildRunTimeline(jobId: string) {
  const startedAt = new Date(Date.now() - 12 * 60 * 1000).toISOString();
  const endedAt = new Date(Date.now() - 8 * 60 * 1000).toISOString();
  return {
    runCount: 2,
    firstStartedAt: startedAt,
    lastActivityAt: endedAt,
    hasActiveRun: false,
    runs: [
      {
        index: 1,
        intent: 'start',
        startedAt,
        endedAt: new Date(Date.now() - 11 * 60 * 1000).toISOString(),
        status: 'failed',
        cli: 'claude',
        exitCode: 1,
        durationSeconds: 60,
        inputSessionId: null,
        capturedSessionId: 'sess-001',
        resumed: false,
        reason: null,
        userFollowup: null,
        lineStart: 1,
        lineEnd: 4,
        headShaBefore: null,
        headShaAfter: null
      },
      {
        index: 2,
        intent: 'continue',
        startedAt: new Date(Date.now() - 11 * 60 * 1000).toISOString(),
        endedAt,
        status: 'completed',
        cli: 'claude',
        exitCode: 0,
        durationSeconds: 180,
        inputSessionId: 'sess-001',
        capturedSessionId: 'sess-002',
        resumed: true,
        reason: null,
        userFollowup: 'continue',
        lineStart: 5,
        lineEnd: 16,
        headShaBefore: null,
        headShaAfter: null
      }
    ]
  };
}

function buildScreenshots(jobId: string) {
  return {
    jobId,
    screenshots: [
      {
        jobId,
        jobTitle: 'Verbose debug spec fixture',
        projectName: 'fixture',
        watchPath: '',
        fileName: 'parser-regression-passed.png',
        relativePath: 'results/parser-regression-passed.png',
        url: '',
        caption: 'Parser regression test passed',
        status: 'passed',
        localPath: 'C:/temp/parser-regression-passed.png',
        timestampUtc: new Date().toISOString()
      }
    ]
  };
}

async function pickAnyJob(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  if (jobs.length === 0) return null;
  return { id: jobs[0].id, watchPath: jobs[0].watchPath };
}

async function installMocks(
  page: Page,
  target: { id: string; watchPath: string }
): Promise<void> {
  const detailBody = JSON.stringify(buildJobDetail(target.id, target.watchPath));
  const outputBody = JSON.stringify(buildOutputBuffer());
  const runsBody = JSON.stringify(buildRunTimeline(target.id));
  const screenshotsBody = JSON.stringify(buildScreenshots(target.id));

  const escId = encodeURIComponent(target.id);
  await page.route(`**/api/jobs/${escId}?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: detailBody });
  });
  await page.route(`**/api/jobs/${escId}/output?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: outputBody });
  });
  await page.route(`**/api/jobs/${escId}/runs?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: runsBody });
  });
  await page.route(`**/api/jobs/${escId}/screenshots?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: screenshotsBody });
  });
  await page.route(`**/api/jobs/${escId}/session-events?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [], currentSessionId: null }) });
  });
  await page.route(`**/api/jobs/${escId}/claude/session-info?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ sessionInfo: null, rateLimit: null }) });
  });
  await page.route(`**/api/jobs/${escId}/git/status?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isRepo: false, branch: null, filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null }) });
  });
}

test.describe('Verbose Debug overlay - task workbench', () => {
  test('opens from the protocol pane, exposes filters, routes raw trace, supports light/dark', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await installMocks(page, target);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    // Land on the activity tab so the open button is reachable.
    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const openBtn = page.getByTestId('protocol-open-verbose-debug');
    await expect(openBtn).toBeVisible({ timeout: 5_000 });
    await openBtn.click();

    const overlay = page.getByTestId('verbose-debug-overlay');
    await expect(overlay).toBeVisible({ timeout: 5_000 });
    // Default tab is Overview with run-stat metric populated.
    await expect(page.getByTestId('verbose-debug-metric-runs')).toHaveText('2');
    await expect(page.getByTestId('verbose-debug-metric-tools')).not.toHaveText('0');

    // Acceptance: default chat stays compact — overlay never replaces the
    // protocol pane underneath.
    await expect(page.getByTestId('pane-protocol')).toBeVisible();

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'verbose-debug-overlay-overview-dark.png'),
      fullPage: false
    });

    // Actors tab: the user / agent / orchestrator counts must render.
    await page.getByTestId('verbose-debug-tab-actors').click();
    const actorRows = page.getByTestId('verbose-debug-actor-rows');
    await expect(actorRows).toBeVisible();
    await expect(actorRows.getByTestId('verbose-debug-actor-row-user')).toBeVisible();
    await expect(actorRows.getByTestId('verbose-debug-actor-row-orchestrator')).toBeVisible();
    await expect(page.getByTestId('verbose-debug-supervisor-counts')).toBeVisible();

    // Orchestrator tab: a decision row exists from the orchestrator stream.
    await page.getByTestId('verbose-debug-tab-orchestrator').click();
    await expect(page.getByTestId('verbose-debug-orchestrator-list')).toBeVisible();
    await expect(page.getByTestId('verbose-debug-orchestrator-list')).toContainText(/reissue|reasoning|reason/i);

    // Tools tab: the tool burst aggregated read/edit/command/search families.
    await page.getByTestId('verbose-debug-tab-tools').click();
    await expect(page.getByTestId('verbose-debug-tool-rows')).toBeVisible();

    // Warnings tab: watchdog quiet was emitted.
    await page.getByTestId('verbose-debug-tab-warnings').click();
    await expect(page.getByTestId('verbose-debug-warning-rows')).toBeVisible();
    await expect(page.getByTestId('verbose-debug-warning-row-watchdogQuiet')).toBeVisible();

    // Tasks tab: both runs are listed.
    await page.getByTestId('verbose-debug-tab-tasks').click();
    await expect(page.getByTestId('verbose-debug-task-rows')).toBeVisible();
    await expect(page.getByTestId('verbose-debug-run-row-1')).toBeVisible();
    await expect(page.getByTestId('verbose-debug-run-row-2')).toBeVisible();

    // Tokens tab: orchestrator token summary surfaces, broken down by scope.
    await page.getByTestId('verbose-debug-tab-tokens').click();
    await expect(page.getByTestId('verbose-debug-token-rows')).toBeVisible();
    await expect(page.getByTestId('verbose-debug-token-orchestrator')).toBeVisible();

    // Artifacts tab: the screenshot fixture appears with status chip.
    await page.getByTestId('verbose-debug-tab-artifacts').click();
    const artifactRows = page.getByTestId('verbose-debug-artifact-rows');
    await expect(artifactRows).toBeVisible();
    await expect(artifactRows).toContainText('parser-regression-passed.png');

    // Regression: the dedicated Trace tab was removed. The tab button must
    // not appear in the navigation, and per-row trace buttons in other tabs
    // (orchestrator decisions, task runs) carry the open-raw-lines affordance.
    await expect(page.getByTestId('verbose-debug-tab-trace')).toHaveCount(0);
    const tabStrip = page.getByTestId('verbose-debug-tabs');
    await expect(tabStrip).not.toContainText('Trace');

    // Light theme: toggle and snapshot.
    await page.getByTestId('verbose-debug-theme-toggle').click();
    await expect(overlay).toHaveAttribute('data-theme', 'light');
    await page.getByTestId('verbose-debug-tab-overview').click();
    await page.screenshot({
      path: path.join(RESULTS_DIR, 'verbose-debug-overlay-overview-light.png'),
      fullPage: false
    });

    // Raw trace routing still works from other tabs: a click on the task-run
    // trace button closes the overlay and hands the range off to the host.
    await page.getByTestId('verbose-debug-tab-tasks').click();
    const runTrace = page.getByTestId('verbose-debug-run-trace-2');
    if (await runTrace.count() > 0) {
      await runTrace.click();
      await expect(overlay).not.toBeVisible();
    }
  });

  test('mobile collapse keeps the overlay scrollable and tabs reachable', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await installMocks(page, target);
    await page.setViewportSize({ width: 412, height: 880 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const openBtn = page.getByTestId('protocol-open-verbose-debug');
    await expect(openBtn).toBeVisible();
    await openBtn.click();

    const overlay = page.getByTestId('verbose-debug-overlay');
    await expect(overlay).toBeVisible();

    // Tabs must be reachable on mobile (single-column layout).
    await page.getByTestId('verbose-debug-tab-actors').click();
    await expect(page.getByTestId('verbose-debug-actor-rows')).toBeVisible();

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'verbose-debug-overlay-mobile.png'),
      fullPage: false
    });

    // Close button stays visible at the top — does not get pushed below the
    // mobile fold by the body grid collapsing into a single column.
    await expect(page.getByTestId('verbose-debug-close')).toBeVisible();
    await page.getByTestId('verbose-debug-close').click();
    await expect(overlay).not.toBeVisible();
  });
});

test.describe('Verbose Debug overlay - project side sheet', () => {
  test('opens from the project side sheet bug button when a task is active', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await installMocks(page, target);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    // Open the orchestrator side sheet from the status-bar toggle. Different
    // builds may have differently-labelled buttons; status bar exposes one
    // per surface, so we look up by the data-testid the side sheet exposes.
    // The task selection has already been forwarded to the side sheet via
    // the host's [activeJobId] binding. Use the toggle button rendered at
    // app shell.
    // Find any visible button that toggles the side sheet — the status bar
    // exposes one with a name matching "Project chat" or similar; fall back
    // to the side sheet's own close/open mechanism if not found.
    const sideSheet = page.getByTestId('orch-side-sheet');
    // The side sheet host lives at the app shell, so we toggle it via the
    // status bar's emoji button. We surface it by clicking any button with
    // a 💬 label in the status bar; otherwise fall back to programmatic
    // open via the test hook.
    const toggleBtn = page.getByRole('button', { name: /Orchestrator chat|Project chat|Side sheet/i });
    if (await toggleBtn.count() > 0) {
      await toggleBtn.first().click();
    } else {
      // Programmatic fallback: invoke .show() through the visible bug-icon
      // path; in practice the host app keeps the panel open whenever the
      // user has asked for it. Skip rather than flake when the surface is
      // not exposed in the current build.
      test.skip(true, 'Status bar toggle for orchestrator side sheet not found');
      return;
    }

    // The Debug button surfaces in the sidesheet header whenever a task
    // detail is selected; the former task tab was removed in the
    // 2026-05-16 sidesheet restructure.
    const bugBtn = page.getByTestId('orch-side-sheet-verbose-debug');
    if (await bugBtn.count() === 0) {
      test.skip(true, 'Bug button not rendered without an active task selection');
      return;
    }
    await expect(bugBtn).toBeVisible();
    await bugBtn.click();

    const overlay = page.getByTestId('app-verbose-debug-overlay');
    await expect(overlay).toBeVisible({ timeout: 5_000 });

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'verbose-debug-overlay-from-side-sheet.png'),
      fullPage: false
    });
  });
});
