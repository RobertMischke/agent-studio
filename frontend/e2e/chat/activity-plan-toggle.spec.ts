import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import type { Page } from '@playwright/test';
import { expect, test } from '../fixtures/dev-backend';

/**
 * Native Codex TODO_LIST proof. The same plan snapshot renders as one living
 * Activity checklist and as the visible task-context progress block in the
 * orchestrator side sheet. Raw provider frames remain available in Trace.
 */
const SHOTS_DIR = path.resolve(
  process.env.JOB_RESULTS_DIR?.trim() || path.resolve(__dirname, '../../test-results/todo-list'),
);
const TARGET = {
  id: 'activity-toolbar-fixture',
  key: 'AGT-2641',
  watchPath: 'C:/fixtures/activity-toolbar',
  project: 'fixture',
};

interface OutLine { timestamp: string; stream: string; text: string; }

function buildOutputBuffer(): OutLine[] {
  const t0 = Date.now() - 60_000;
  const t = (seconds: number) => new Date(t0 + seconds * 1000).toISOString();
  return [
    { timestamp: t(0), stream: 'stdout', text: '{"type":"item.started","item":{"type":"todo_list","items":[{"text":"Inspect the Activity projection","completed":false},{"text":"Integrate live task progress","completed":false},{"text":"Run the verification suite","completed":false}]}}' },
    { timestamp: t(2), stream: 'stdout', text: '* Read protocol-pane.component.ts' },
    { timestamp: t(5), stream: 'stdout', text: '{"type":"item.updated","item":{"type":"todo_list","items":[{"text":"Inspect the Activity projection","completed":true},{"text":"Integrate live task progress","completed":false},{"text":"Run the verification suite","completed":false}]}}' },
    { timestamp: t(8), stream: 'stdout', text: 'The live projection now uses the current snapshot.' },
  ];
}

function buildPlan() {
  const updatedAt = new Date(Date.now() - 52_000).toISOString();
  return {
    hasPlan: true,
    source: 'codex/todo_list',
    snapshotCount: 2,
    updatedAt,
    activeItemId: 'integrate',
    softEstimateMedian: null,
    items: [
      { id: 'inspect', title: 'Inspect the Activity projection', status: 'done', subActionCount: 1, subActions: [] },
      { id: 'integrate', title: 'Integrate live task progress', status: 'active', subActionCount: 2, subActions: [] },
      { id: 'verify', title: 'Run the verification suite', status: 'pending', subActionCount: 0, subActions: [] },
    ],
    unassignedSubActions: [],
  };
}

function detail() {
  return {
    info: {
      id: TARGET.id,
      key: TARGET.key,
      taskKey: TARGET.key,
      displayKey: TARGET.key,
      title: 'Native TODO list integration',
      state: '3-progress',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5',
      watchPath: TARGET.watchPath,
      projectName: TARGET.project,
      folderPath: `${TARGET.watchPath}/.orchestrator/jobs/3-progress/${TARGET.id}`,
      createdAt: '2026-08-11T08:00:00.000Z',
      lastActivity: '2026-08-11T08:05:00.000Z',
      sessionName: null,
      useOwnSession: null,
      lastUsage: null,
      execution: {
        status: 'running',
        startedAt: '2026-08-11T08:04:00.000Z',
        model: 'gpt-5',
        cliType: 'codex',
      },
      commit: null,
      commits: [],
      codeActivityDetected: true,
      summaryState: null,
      taskType: 'feature',
      tags: [],
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    },
    promptMarkdown: 'Integrate the native Codex TODO list.',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const encodedId = encodeURIComponent(TARGET.id);
  const plan = buildPlan();
  await page.route('**/api/**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/healthz**', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/auth/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/crash-recovery/pending**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.route('**/api/projects/*/workbenches**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ projectName: TARGET.project, items: [] }),
  }));
  await page.route('**/api/tasks/reference-status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route('**/api/tasks/grouped**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [],
      progress: [detail().info], failedPickup: [], codeNotComplete: [],
      autoReview: [], humanReview: [], review: [], completed: [], archive: [],
    }),
  }));
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: TARGET.project, path: TARGET.watchPath, rootPath: TARGET.watchPath }]),
  }));
  await page.route('**/api/runner/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      projects: {
        [TARGET.project]: {
          projectName: TARGET.project,
          mode: 'manual',
          activeJobId: TARGET.id,
          activeExecution: detail().info.execution,
          queuedJobIds: [],
        },
      },
    }),
  }));
  await page.route('**/api/cli/quota**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
  }));
  await page.route(`**/api/tasks/${encodedId}/output**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(buildOutputBuffer()),
  }));
  await page.route(`**/api/tasks/${encodedId}/plan**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(plan),
  }));
  await page.route(`**/api/tasks/${encodedId}/pipeline**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pipeline: { pre: [], core: [], post: [], allSteps: [] }, execution: null, executions: [], config: {}, cost: null }),
  }));
  await page.route(`**/api/tasks/${encodedId}/runs**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ runs: [] }),
  }));
  await page.route(`**/api/tasks/${encodedId}/session-events**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ events: [], sessionChain: [] }),
  }));
  await page.route(`**/api/tasks/${encodedId}/claude-session**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: 'null',
  }));
  await page.route(`**/api/tasks/${encodedId}?**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(detail()),
  }));
  await page.route('**/api/orchestrator/sessions**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ sessions: [] }),
  }));
  await page.route('**/api/orchestrator/context/**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      contextKey: `task:${TARGET.project}/${TARGET.key}`,
      capturedAt: new Date().toISOString(),
      digest: 'task focus: AGT-2641; progress run active',
      sources: [],
      taskPlan: plan,
    }),
  }));
  await page.route('**/api/runner/**/orchestrator-chat**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ project: TARGET.project, turns: [] }),
  }));
}

async function setTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate(value => {
    localStorage.setItem('atp.studio.theme', value);
    document.documentElement.setAttribute('data-studio-theme', value);
  }, theme);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
}

test('TODO_LIST is one live Activity checklist and visible orchestrator context in both themes', async ({ page, devBackend: _ }) => {
  mkdirSync(SHOTS_DIR, { recursive: true });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.nextGenChat', '1');
    localStorage.setItem('atp.studio.theme', 'light');
  });
  await installRoutes(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);
  await page.getByTestId('inspector-tab-activity').click();

  const checklist = page.getByTestId('conversation-plan-update');
  await expect(checklist).toHaveCount(1);
  await expect(page.getByTestId('plan-progress')).toHaveText('1/3');
  await expect(checklist.locator('[data-status="completed"]')).toContainText('Inspect the Activity projection');
  await expect(checklist.locator('[data-status="in_progress"]')).toContainText('Integrate live task progress');
  await expect(checklist.locator('[data-status="pending"]')).toContainText('Run the verification suite');
  await expect(page.getByTestId('activity-view-tab-plan')).toHaveCount(0);
  await expect(page.getByTestId('activity-panel').getByTestId('conversation-view'))
    .not.toContainText('"type":"item.updated"');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('activity-panel').screenshot({
      path: path.join(SHOTS_DIR, `todo-list-activity-${theme}.png`),
    });
  }

  await page.getByTestId('activity-toolbar-menu').click();
  await page.getByTestId('activity-toolbar-menu-item-trace').click();
  await expect(page.getByTestId('activity-log-trace')).toContainText('Codex item.updated todo_list');
  await page.getByTestId('next-gen-chat-trace-back').click();
  await expect(checklist).toHaveCount(1);

  if (await page.locator('app-orchestrator-side-sheet.is-open').count() === 0) {
    await page.getByTestId('orch-side-sheet-toggle').click();
  }
  const progress = page.getByTestId('orchestrator-task-progress');
  await expect(progress).toBeVisible();
  await expect(page.getByTestId('orchestrator-task-progress-count')).toContainText('1/3 complete');
  await expect(progress.locator('[data-status="active"]')).toContainText('Integrate live task progress');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('orch-side-sheet').screenshot({
      path: path.join(SHOTS_DIR, `todo-list-orchestrator-${theme}.png`),
    });
  }
});
