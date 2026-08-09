import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env.JOB_RESULTS_DIR
  ?? path.resolve(__dirname, '../../test-results/activity-protocol-density');
const EVIDENCE_VARIANT = process.env.EVIDENCE_VARIANT === 'before' ? 'before' : 'after';
const TARGET = {
  id: 'mkt-20-protocol-density',
  watchPath: 'C:/Projects/Agent-Studio-Marketing',
  worktreePath: 'C:/Temp/ass-worktrees/MKT-20',
};

interface OutLine {
  timestamp: string;
  stream: string;
  text: string;
}

function outputBuffer(): OutLine[] {
  const origin = Date.parse('2026-08-09T21:40:00.000Z');
  const at = (seconds: number) => new Date(origin + seconds * 1_000).toISOString();
  const output: OutLine[] = [
    { timestamp: at(0), stream: 'user', text: 'Condense the marketing protocol while preserving failures.' },
    { timestamp: at(2), stream: 'stdout', text: 'I will inspect the existing protocol and update the projection.' },
  ];

  for (let index = 0; index < 4; index++) {
    output.push({
      timestamp: at(5 + index),
      stream: 'stdout',
      text: `* Run npm --prefix frontend run check:${index + 1}`,
    });
  }
  output.push(
    { timestamp: at(10), stream: 'stdout', text: '* Read frontend/src/app/features/task-detail/protocol-pane.ts' },
    { timestamp: at(11), stream: 'stdout', text: '* Read frontend/src/app/features/task-detail/protocol-pane.spec.ts' },
    { timestamp: at(100), stream: 'stdout', text: 'The call sites are mapped. I will apply the focused edits now.' },
  );

  const fullPath = `${TARGET.worktreePath}/frontend/src/app/features/task-detail/activity-event-presentation.ts`;
  for (let index = 0; index < 5; index++) {
    output.push({
      timestamp: at(105 + index),
      stream: 'stdout',
      text: `* Edit ${fullPath}`,
    });
  }
  output.push(
    { timestamp: at(120), stream: 'stdout', text: 'The projection is updated and the regression checks are running.' },
    {
      timestamp: at(155),
      stream: 'orchestrator',
      text: '[watchdog-warning] no output for 35s [phase=TurnInProgress silence=35s allowed=180/600s]',
    },
    { timestamp: at(158), stream: 'orchestrator', text: '[watchdog-warning] streaming output again' },
    {
      timestamp: at(198),
      stream: 'orchestrator',
      text: '[watchdog-warning] no output for 40s [phase=TurnInProgress silence=40s allowed=180/600s]',
    },
    { timestamp: at(201), stream: 'orchestrator', text: '[watchdog-warning] streaming output again' },
    {
      timestamp: at(249),
      stream: 'orchestrator',
      text: '[watchdog-warning] no output for 48s [phase=TurnInProgress silence=48s allowed=180/600s]',
    },
    { timestamp: at(252), stream: 'orchestrator', text: '[watchdog-warning] streaming output again' },
    {
      timestamp: at(852),
      stream: 'orchestrator',
      text: '[watchdog-timeout] auto-cancelled after 600s of silence [phase=TurnInProgress silence=600s allowed=180/600s]',
    },
  );
  return output;
}

function taskInfo() {
  return {
    id: TARGET.id,
    taskKey: 'MKT-20',
    displayKey: 'MKT-20',
    title: 'Marketing protocol density fixture',
    state: '5-human-review',
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-7',
    thinkingLevel: 'high',
    watchPath: TARGET.watchPath,
    projectName: 'marketing-fixture',
    folderPath: `${TARGET.watchPath}/5-human-review/${TARGET.id}`,
    createdAt: '2026-08-09T21:39:00.000Z',
    lastActivity: '2026-08-09T21:54:12.000Z',
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    codeActivityDetected: true,
    taskType: 'feature',
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

function detail() {
  return {
    info: taskInfo(),
    promptMarkdown: 'Condense the protocol.',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

function projectInfo() {
  return {
    id: 'marketing-fixture',
    displayName: 'Marketing',
    shortCode: 'MKT',
    workspaceId: 'workspace-fixture',
    storageLocation: TARGET.watchPath,
    rootPath: TARGET.watchPath,
    repositoryPath: TARGET.watchPath,
    sortOrder: 0,
    archived: false,
    urls: [],
  };
}

function runTimeline() {
  return {
    runCount: 1,
    firstStartedAt: '2026-08-09T21:40:00.000Z',
    lastActivityAt: '2026-08-09T21:54:12.000Z',
    hasActiveRun: false,
    runs: [{
      index: 1,
      intent: 'start',
      startedAt: '2026-08-09T21:40:00.000Z',
      endedAt: '2026-08-09T21:54:12.000Z',
      status: 'failed',
      cli: 'claude',
      model: 'claude-opus-4-7',
      thinkingLevel: 'high',
      executionLocation: {
        state: 'no-active-execution',
        executionKind: 'none',
        worktreePath: TARGET.worktreePath,
        connectionState: 'closed',
        leaseState: 'released',
        trustReason: 'fixture',
        historical: true,
      },
      exitCode: 1,
      durationSeconds: 852,
      inputSessionId: null,
      capturedSessionId: 'mkt-20-session',
      resumed: false,
      reason: 'watchdog',
      userFollowup: null,
      lineStart: 1,
      lineEnd: outputBuffer().length,
      headShaBefore: '1111111111111111111111111111111111111111',
      headShaAfter: '2222222222222222222222222222222222222222',
      contextRef: null,
    }],
    promptEntries: [],
    runnerEvents: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const encodedId = encodeURIComponent(TARGET.id);
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/auth/status', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/tasks/grouped**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [taskInfo()],
      review: [], completed: [], archive: [],
    }),
  }));
  await page.route('**/api/watch-paths**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: 'marketing-fixture', path: TARGET.watchPath, rootPath: TARGET.watchPath }]),
  }));
  await page.route('**/api/projects', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([projectInfo()]),
  }));
  await page.route('**/api/workspaces', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{
      id: 'workspace-fixture', displayName: 'Fixture', color: '#6c8cff', projects: [projectInfo()],
    }]),
  }));
  await page.route('**/api/runner/status**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      projects: {
        'marketing-fixture': {
          projectName: 'marketing-fixture',
          mode: 'manual',
          activeJobId: null,
          activeExecution: null,
          queuedJobIds: [],
        },
      },
    }),
  }));
  await page.route('**/api/cli/quota**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
  }));
  await page.route(`**/api/tasks/${encodedId}/output**`, (route) => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(outputBuffer()),
  }));
  await page.route(`**/api/tasks/${encodedId}/runs**`, (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(runTimeline()),
  }));
  await page.route(`**/api/tasks/${encodedId}/plan**`, (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ hasPlan: false, items: [], unassignedSubActions: [] }),
  }));
  await page.route(`**/api/tasks/${encodedId}/pipeline**`, (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pipeline: { pre: [], core: [], post: [], allSteps: [] }, execution: null, executions: [] }),
  }));
  await page.route(`**/api/tasks/${encodedId}/session-events**`, (route) => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }),
  }));
  await page.route(`**/api/tasks/${encodedId}/screenshots**`, (route) => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ jobId: TARGET.id, screenshots: [] }),
  }));
  await page.route(`**/api/tasks/${encodedId}/runs/1/files**`, (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ files: [{ path: 'frontend/src/app/features/task-detail/activity-event-presentation.ts', status: 'M' }] }),
  }));
  await page.route(`**/api/tasks/${encodedId}?**`, (route) => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(detail()),
  }));
}

test.describe('Activity protocol density', () => {
  test('keeps terminal waits separate and enriches the compact activity rows', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('atp.flag.nextGenChat', '1'));
    await installRoutes(page);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);
    await dismissDevErrorDialog(page);
    const activityTab = page.getByTestId('inspector-tab-activity');
    await activityTab.click();
    await expect(activityTab).toHaveAttribute('aria-selected', 'true');

    const conversation = page.getByTestId('pane-protocol').getByTestId('conversation-view');
    await expect(conversation).toBeVisible();

    if (EVIDENCE_VARIANT === 'before') {
      await expect(conversation.getByTestId('conversation-supervisor-wait')).toHaveCount(7);
      await expect(conversation.getByTestId('conversation-tool-burst')).toHaveCount(2);
    } else {
      const waits = conversation.getByTestId('conversation-supervisor-wait');
      await expect(waits).toHaveCount(2);
      await expect(waits.first()).toContainText('6× quiet/resumed');
      await expect(waits.first()).toContainText('longest silence 48s');
      await expect(waits.first()).toContainText('allowed 180s');
      await expect(waits.last()).toHaveAttribute('data-state', 'killed');

      const tool = conversation.locator('[data-category="activity-tool-summary"]');
      await expect(tool).toContainText('6 Tool calls');
      await expect(tool).toContainText('shell ×4, read ×2');
      await expect(tool).toContainText('all ok');

      const edit = conversation.locator('[data-category="activity-edit-summary"]');
      await expect(edit).toContainText('5 Edits · 1 file');
      await expect(edit).toContainText('frontend/src/app/features/task-detail/activity-event-presentation.ts');
      await expect(edit.getByTestId('activity-edit-files')).toHaveAttribute('title', /C:\/Temp\/ass-worktrees\/MKT-20/);
      const diffAction = edit.getByRole('button', { name: 'Open commit diff' });
      await expect(diffAction).toBeVisible();
      await diffAction.click();
      const diffDialog = page.getByRole('dialog', { name: 'Run git viewer' });
      await expect(diffDialog).toBeVisible();
      await diffDialog.getByRole('button', { name: 'Close' }).click();
      await expect(diffDialog).toHaveCount(0);
    }

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await conversation.screenshot({
        path: path.join(RESULTS_DIR, `protocol-density-${EVIDENCE_VARIANT}-${theme}--mocked.png`),
      });
    }
  });
});
