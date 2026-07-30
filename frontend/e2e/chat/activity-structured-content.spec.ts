import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

const SHOTS_DIR = path.resolve(__dirname, '../../results/AGT-2433');
const TARGET = {
  id: 'activity-structured-content-fixture',
  watchPath: 'C:/fixtures/activity-structured-content',
};

interface OutLine {
  timestamp: string;
  stream: string;
  text: string;
}

function outputBuffer(): OutLine[] {
  const t0 = Date.parse('2026-07-28T10:40:44.000Z');
  const at = (offset: number) => new Date(t0 + offset * 1000).toISOString();
  return [
    { timestamp: at(0), stream: 'system', text: "[runner] working tree ready on branch 'main'" },
    { timestamp: at(1), stream: 'system', text: '[runner] spawning codex exec -m gpt-5.6-sol -' },
    { timestamp: at(2), stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
    { timestamp: at(3), stream: 'stderr', text: '--------' },
    { timestamp: at(4), stream: 'stderr', text: 'workdir: /workspace/AGT-2355' },
    { timestamp: at(5), stream: 'stderr', text: 'user' },
    { timestamp: at(6), stream: 'stderr', text: 'Create the Deck icon exploration.' },
    { timestamp: at(7), stream: 'stderr', text: 'codex' },
    { timestamp: at(8), stream: 'stderr', text: 'I will inspect the current work and verify the generated files.' },
    { timestamp: at(9), stream: 'system', text: '[runner-log-delivery:fixture-1]' },
    { timestamp: at(10), stream: 'stderr', text: 'exec' },
    {
      timestamp: at(11),
      stream: 'stderr',
      text: '/bin/bash -lc "git diff -- docs/start/README.md docs/operations/deck-icon-exploration/workbench.json"',
    },
    { timestamp: at(12), stream: 'stderr', text: ' succeeded in 18ms:' },
    {
      timestamp: at(13),
      stream: 'stderr',
      text: 'diff --git a/docs/start/README.md b/docs/start/README.md',
    },
    { timestamp: at(14), stream: 'stderr', text: '+{' },
    {
      timestamp: at(15),
      stream: 'stderr',
      text: '+  "title": "Apply Robert\'s selected Deck icon",',
    },
    { timestamp: at(16), stream: 'stderr', text: '+}' },
    { timestamp: at(17), stream: 'system', text: '[runner-log-delivery:fixture-2]' },
    { timestamp: at(18), stream: 'stderr', text: 'codex' },
    {
      timestamp: at(19),
      stream: 'stderr',
      text: 'I will inspect the Wiki concept document before wrapping up.',
    },
    { timestamp: at(20), stream: 'stderr', text: 'read_file' },
    {
      timestamp: at(21),
      stream: 'stderr',
      text: 'docs/concepts/wiki-concept.html',
    },
    { timestamp: at(22), stream: 'stderr', text: ' succeeded in 12ms:' },
    { timestamp: at(23), stream: 'stderr', text: '<!doctype html>' },
    { timestamp: at(24), stream: 'stderr', text: '<html lang="en">' },
    { timestamp: at(25), stream: 'stderr', text: '<head>' },
    { timestamp: at(26), stream: 'stderr', text: '  <meta charset="utf-8">' },
    { timestamp: at(27), stream: 'stderr', text: '  <style>' },
    { timestamp: at(28), stream: 'stderr', text: '    .concept-grid {' },
    { timestamp: at(29), stream: 'stderr', text: '      display: grid;' },
    { timestamp: at(30), stream: 'stderr', text: '      grid-template-columns: 1fr 1fr;' },
    { timestamp: at(31), stream: 'stderr', text: '    }' },
    { timestamp: at(32), stream: 'stderr', text: '  </style>' },
    { timestamp: at(33), stream: 'stderr', text: '</head>' },
    { timestamp: at(34), stream: 'stderr', text: '<body>' },
    {
      timestamp: at(35),
      stream: 'stderr',
      text: '  <p class="lead">The concept remains readable as source.</p>',
    },
    { timestamp: at(36), stream: 'stderr', text: '</body>' },
    { timestamp: at(37), stream: 'stderr', text: '</html>' },
    { timestamp: at(38), stream: 'stderr', text: 'codex' },
    {
      timestamp: at(39),
      stream: 'stderr',
      text: 'The concept and its navigation entry are ready for review.',
    },
    { timestamp: at(40), stream: 'stderr', text: '[[TASK_DONE]]' },
    {
      timestamp: at(41),
      stream: 'system',
      text: '[runner] CLI exited 0; typedOutcome=ExplicitAgentDone classifier=execution-outcome/v1',
    },
  ];
}

function detail() {
  return {
    info: {
      id: TARGET.id,
      taskKey: `ASS-E2E-${TARGET.id}`,
      displayKey: 'ASS-E2E',
      title: 'Activity structured content fixture',
      state: '5-human-review',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.6-sol',
      thinkingLevel: 'medium',
      watchPath: TARGET.watchPath,
      projectName: 'fixture',
      folderPath: `${TARGET.watchPath}/tasks/${TARGET.id}`,
      createdAt: '2026-07-28T10:40:00.000Z',
      lastActivity: '2026-07-28T10:41:10.000Z',
      sessionName: null,
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      codeActivityDetected: true,
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

async function installRoutes(page: Page): Promise<void> {
  const encodedId = encodeURIComponent(TARGET.id);
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
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
      body: JSON.stringify({
        projects: {
          fixture: {
            projectName: 'fixture',
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }));
  await page.route(`**/api/tasks/${encodedId}/output**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(outputBuffer()),
    }));
  await page.route(`**/api/tasks/${encodedId}/plan**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasPlan: false,
        source: null,
        snapshotCount: 0,
        activeItemId: null,
        softEstimateMedian: null,
        items: [],
        unassignedSubActions: [],
      }),
    }));
  await page.route(`**/api/tasks/${encodedId}/pipeline**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        pipeline: { pre: [], core: [], post: [], allSteps: [] },
        execution: null,
        executions: [],
        config: {},
        cost: null,
      }),
    }));
  await page.route(`**/api/tasks/${encodedId}/runs**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ runs: [], runnerEvents: [] }),
    }));
  await page.route(`**/api/tasks/${encodedId}/session-events**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }));
  await page.route(`**/api/tasks/${encodedId}/claude-session**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(`**/api/tasks/${encodedId}?**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(detail()),
    }));
}

test('Activity renders structured tool payloads and runner events quietly', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.nextGenChat', '1');
    localStorage.setItem('atp.studio.theme', 'light');
  });
  await installRoutes(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(
    `/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`,
  );
  await page.getByTestId('inspector-tab-activity').click();

  const panel = page.getByTestId('activity-panel');
  await expect(panel).toBeVisible();
  const runtimeDialog = page.getByTestId('error-dialog-overlay');
  if (await runtimeDialog.isVisible()) {
    await runtimeDialog.getByRole('button').first().click();
  }
  await expect(panel.getByTestId('conversation-view')).toBeVisible();

  const tools = panel.getByTestId('tool-burst-chip');
  await expect(tools).toHaveCount(2);
  const diffTool = tools.nth(0);
  await diffTool.getByTestId('tool-burst-row').click();
  const diffOutput = diffTool.getByTestId('tool-burst-command-output');
  await expect(diffOutput).toContainText('diff --git a/docs/start/README.md');
  await expect(diffOutput).toContainText('"title": "Apply Robert\'s selected Deck icon"');

  const markupTool = tools.nth(1);
  await expect(markupTool.getByTestId('tool-burst-row')).toHaveAttribute('aria-expanded', 'false');
  await markupTool.getByTestId('tool-burst-row').click();
  const markupOutput = markupTool.getByTestId('tool-burst-command-output');
  await expect(markupOutput).toContainText('<meta charset="utf-8">');
  await expect(markupOutput).toContainText('<p class="lead">');
  await expect(markupTool.getByTestId('tool-burst-files')).toContainText(
    'docs/concepts/wiki-concept.html',
  );
  await expect(markupOutput.locator('li')).toHaveCount(0);
  expect((await panel.getByTestId('conversation-message-item').allTextContents()).join('\n'))
    .not.toContain('<meta charset=');
  await panel.screenshot({
    path: path.join(SHOTS_DIR, 'activity-html-after.png'),
  });
  const runnerRows = panel.locator('[data-testid="conversation-system-status"][data-category="runner"]');
  await expect(runnerRows).toHaveCount(3);
  await expect(runnerRows.first()).not.toContainText('[runner]');
  await expect(panel).not.toContainText('[runner-log-delivery:');
});
