import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

const TARGET = { id: 'codex-jsonl-fixture', watchPath: 'C:/fixtures/repo' };
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

const OUTPUT = [
  line('{"type":"turn.started"}'),
  line('{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"I will make the frontend change.\\n\\n- Parser first\\n- UI check second"}}'),
  line('{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"git status --short","aggregated_output":"","exit_code":null,"status":"in_progress"}}'),
  line('{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"git status --short","aggregated_output":"","exit_code":0,"status":"completed"}}'),
];

function line(text: string, stream = 'stdout', timestamp = '2026-06-07T12:00:00.000Z') {
  return { timestamp, stream, text };
}

function detail() {
  return {
    info: {
      id: TARGET.id,
      taskKey: `ASS-E2E-${TARGET.id}`,
      displayKey: 'ASS-E2E',
      title: 'Codex JSONL Activity Log fixture',
      state: '5-human-review',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5',
      watchPath: TARGET.watchPath,
      projectName: 'fixture',
      folderPath: `${TARGET.watchPath}/.orchestrator/jobs/5-human-review/${TARGET.id}`,
      createdAt: '2026-06-07T12:00:00.000Z',
      lastActivity: '2026-06-07T12:00:00.000Z',
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

async function installRoutes(page: Page) {
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
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/output?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(OUTPUT) }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/runs?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/session-events?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/claude-session?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail()) }));
}

async function evidence(page: Page, fileName: string) {
  if (!RESULTS_DIR) return;
  await page.screenshot({ path: path.join(RESULTS_DIR, fileName), fullPage: false });
}

test('Codex JSONL Activity Log Conversation renders readable agent text and summarized tools', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 980 });
  await page.addInitScript(() => {
    localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
  });
  await installRoutes(page);

  await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);
  await page.getByTestId('activity-log-mode-conversation').click({ force: true });

  const conversation = page.getByTestId('activity-log-conversation');
  await expect(conversation).toContainText('I will make the frontend change.');
  await expect(conversation).not.toContainText('{"type"');
  await expect(page.getByTestId('convo-turn-tools')).toHaveCount(0);
  await evidence(page, 'codex-activity-log-conversation.png');

  await page.getByTestId('activity-log-show-tools').click();
  await expect(page.getByTestId('convo-turn-tools')).toBeVisible();
  await expect(conversation).toContainText('Commands');
  await expect(conversation).not.toContainText('{"type"');
  await evidence(page, 'codex-activity-log-tools-visible.png');

  await page.getByTestId('activity-log-mode-trace').click({ force: true });
  await expect(page.getByTestId('activity-log-trace')).toContainText('git status --short');
  await expect(page.getByTestId('activity-log-trace')).not.toContainText('{"type"');
  await evidence(page, 'codex-activity-log-trace.png');
});
