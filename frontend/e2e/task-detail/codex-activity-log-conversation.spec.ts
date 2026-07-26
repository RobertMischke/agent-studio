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

const REVIEW_OUTPUT = [
  line(
    '[19:23:30.120] [supervisor] [escalate] Auto-review completion gate could not clear unfinished work.',
    'stderr',
    '2026-07-24T19:23:30.120Z',
  ),
  line(
    '[reissue] Completion evidence needs one more pass.',
    'orchestrator',
    '2026-07-24T19:23:31.120Z',
  ),
  line('Implemented the requested Activity fixes.', 'stderr', '2026-07-24T19:23:32.120Z'),
  ...Array.from({ length: 42 }, (_, index) =>
    line(
      `  - Evidence line ${index + 1}: the complete agent response remains available.`,
      'stderr',
      `2026-07-24T19:23:${String(33 + Math.min(index, 26)).padStart(2, '0')}.120Z`,
    )),
  line('  tokens used', 'stderr', '2026-07-24T19:23:59.220Z'),
  line('  60,162', 'stderr', '2026-07-24T19:23:59.320Z'),
  line('Error: command exited with code 1', 'stderr', '2026-07-24T19:24:00.120Z'),
];

function line(text: string, stream = 'stdout', timestamp = '2026-06-07T12:00:00.000Z') {
  return { timestamp, stream, text };
}

function detail(queued = false) {
  return {
    info: {
      id: TARGET.id,
      taskKey: `ASS-E2E-${TARGET.id}`,
      displayKey: 'ASS-E2E',
      title: 'Completion judge: semantically interpret final-attempt prose with typed evidence',
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
      pendingIntent: queued ? {
        version: 1,
        mode: 'continue',
        prompt: 'Finish the queued Activity review.',
        savedAt: '2026-07-24T19:24:01.120Z',
        savedReason: 'project-busy',
        savedAgainstActiveJobId: 'other-task',
      } : null,
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

async function installRoutes(
  page: Page,
  output = OUTPUT,
  queued = false,
) {
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
  await page.route('**/api/cli/quota', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        at: '2026-07-24T19:23:30.120Z',
        ttlSeconds: 600,
        snapshots: [],
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
        humanReview: [detail(queued).info],
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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(output) }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/runs?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/pipeline?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/session-events?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}/claude-session?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(`**/api/tasks/${encodeURIComponent(TARGET.id)}?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail(queued)) }));
}

async function evidence(page: Page, fileName: string) {
  if (!RESULTS_DIR) return;
  await page.screenshot({ path: path.join(RESULTS_DIR, fileName), fullPage: false });
}

test('Codex JSONL Activity Log Conversation renders readable agent text and summarized tools', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 980 });
  await page.addInitScript(() => {
    localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
    // Exercises the LEGACY activity-log-view's conversation mode; pin
    // Frontend:NextGenChat off ('0') now that it defaults ON.
    localStorage.setItem('atp.flag.nextGenChat', '0');
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

for (const theme of ['light', 'dark'] as const) {
  test(`Activity review output stays structured and expandable in ${theme} theme`, async ({ page }) => {
    test.slow();
    page.setDefaultNavigationTimeout(45_000);
    await page.setViewportSize({ width: 1500, height: 980 });
    await page.addInitScript((selectedTheme) => {
      localStorage.setItem('atp.studio.theme', selectedTheme);
      localStorage.setItem('atp.flag.nextGenChat', '1');
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({
        prompt: true,
        protocol: true,
        git: false,
      }));
    }, theme);
    await installRoutes(page, REVIEW_OUTPUT, true);

    await page.goto(
      `/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`,
      { waitUntil: 'domcontentloaded' },
    );
    await page.getByTestId('inspector-tab-activity').click();

    const conversation = page
      .getByTestId('activity-panel')
      .getByTestId('conversation-view');
    await expect(conversation).toBeVisible();

    const supervisor = conversation.getByTestId('conversation-message-message.supervisor');
    await expect(supervisor).toContainText('Supervisor');
    await expect(supervisor).toContainText('Escalate');
    await expect(supervisor).toContainText('Auto-review completion gate');
    await expect(supervisor).not.toContainText('[supervisor]');
    await expect(supervisor.getByTestId('conversation-message-time')).toBeVisible();

    const cliFailures = conversation
      .getByTestId('conversation-system-status')
      .filter({ hasText: 'CLI failed' });
    await expect(cliFailures).toHaveCount(1);
    await expect(cliFailures).toContainText('Error: command exited with code 1');
    await expect(conversation).toContainText('tokens used');
    await expect(conversation).toContainText('60,162');

    const agentItem = conversation
      .getByTestId('conversation-message-message.taskAgent')
      .getByTestId('conversation-message-item');
    await expect(agentItem).toHaveAttribute('data-collapsed', 'true');
    const collapsedBox = await agentItem.boundingBox();

    const decision = conversation.getByTestId('conversation-decision-orchestrator');
    await expect(decision).toContainText('Reissue');
    await expect(decision.getByText('→ reissue', { exact: true })).toHaveCount(0);
    await expect(decision.getByTestId('conversation-decision-open-trace')).toBeVisible();
    const decisionBox = await decision.boundingBox();
    const feedBox = await conversation.getByTestId('conversation-feed').boundingBox();
    expect(decisionBox?.width ?? 0).toBeGreaterThan((feedBox?.width ?? 0) * 0.9);

    await expect(conversation.getByTestId('conversation-task-marker')).toHaveCount(0);
    await expect(conversation.getByTestId('conversation-status-queued')).toContainText(
      /Queued\s+Will run on the next pickup\./,
    );
    await evidence(page, `activity-review-${theme}-collapsed.png`);

    await agentItem.getByTestId('conversation-message-item-expand').click();
    await expect(agentItem).toHaveAttribute('data-collapsed', 'false');
    await expect(agentItem.getByTestId('conversation-message-item-expand')).toHaveAttribute(
      'aria-expanded',
      'true',
    );
    const expandedBox = await agentItem.boundingBox();
    expect(expandedBox?.height ?? 0).toBeGreaterThan((collapsedBox?.height ?? 0) * 1.5);
    await evidence(page, `activity-review-${theme}-expanded.png`);
  });
}
