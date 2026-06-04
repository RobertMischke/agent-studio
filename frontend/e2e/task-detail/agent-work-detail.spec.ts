import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Overview tab — Agent Work drill-down (grouped, expandable tool detail).
 *
 * The Agent Work block used to expose only per-tool count chips (Bash 68,
 * Read 68, …) with no way to see *what* the agent actually did. This spec
 * covers the new `<app-agent-work-detail>` disclosure: a lazily-loaded,
 * grouped, expandable view where each tool group expands to its individual
 * calls (command / file / pattern + pass/fail outcome).
 *
 * Pattern follows activity-plan-toggle: drive the live frontend (proxied to
 * a real backend) but pin only the two evidence routes the block keys off —
 * `agent-work-summary` (so the section renders with tool calls) and
 * `agent-work-detail` (the grouped drill-down). Everything else is served by
 * the backend the frontend proxies to.
 *
 * Screenshot lands under JOB_RESULTS_DIR when the orchestrator sets it,
 * else test-results/ (scratch).
 */

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  || path.resolve(__dirname, '../../test-results');

const SUMMARY = {
  calls: 3,
  recovered: false,
  toolCalls: 218,
  toolCounts: [
    { tool: 'Bash', count: 68 },
    { tool: 'Read', count: 68 },
    { tool: 'Grep', count: 44 },
    { tool: 'Edit', count: 36 },
    { tool: 'Write', count: 4 },
  ],
  startedAt: new Date(Date.now() - 12 * 60 * 1000).toISOString(),
  lastTouchAt: new Date(Date.now() - 90 * 1000).toISOString(),
  currentSessionId: 'sess-fixture-agent-work',
};

function call(offsetSec: number, argument: string, isError = false, firstLine: string | null = null) {
  return {
    ts: new Date(Date.now() - offsetSec * 1000).toISOString(),
    argument,
    completed: true,
    isError,
    resultFirstLine: firstLine,
  };
}

const DETAIL = {
  totalCalls: 220,
  groups: [
    {
      tool: 'Bash',
      count: 68,
      calls: [
        call(600, 'npm test', false, '42 passing'),
        call(540, 'git status --short'),
        call(480, 'dotnet test --artifacts-path /tmp/atp', true, 'MSB1009: Project file does not exist'),
      ],
    },
    {
      tool: 'Read',
      count: 68,
      calls: [
        call(560, 'backend/Services/Tasks/AgentWorkSummaryReader.cs'),
        call(500, 'frontend/src/app/features/session-events/models/session-events.model.ts'),
      ],
    },
    {
      tool: 'Grep',
      count: 44,
      calls: [call(430, 'AgentWorkSummary'), call(420, 'hasAgentWork')],
    },
    {
      tool: 'Edit',
      count: 36,
      calls: [call(360, 'overview-pane.component.html'), call(350, 'task.service.ts')],
    },
    { tool: 'Write', count: 4, calls: [call(300, 'agent-work-detail.component.ts')] },
  ],
};

async function pinEvidence(page: Page, jobId: string): Promise<void> {
  const esc = encodeURIComponent(jobId);
  const summary = JSON.stringify(SUMMARY);
  const detail = JSON.stringify(DETAIL);
  for (const base of ['tasks', 'jobs']) {
    await page.route(`**/api/${base}/${esc}/agent-work-summary**`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: summary }));
    await page.route(`**/api/${base}/${esc}/agent-work-detail**`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: detail }));
  }
}

async function pickJob(page: Page): Promise<{ id: string; watchPath: string } | null> {
  const res = await page.request.get('/api/tasks');
  if (!res.ok()) return null;
  const jobs = (await res.json()) as { id: string; watchPath: string }[];
  if (!Array.isArray(jobs) || jobs.length === 0) return null;
  return { id: jobs[0].id, watchPath: jobs[0].watchPath };
}

async function openOverview(page: Page, job: { id: string; watchPath: string }): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
  await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 20_000 });
  const overviewTab = page.getByTestId('prompt-tab-overview');
  await expect(overviewTab).toBeVisible({ timeout: 20_000 });
  await overviewTab.click();
  await expect(page.getByTestId('overview-tab')).toBeVisible();
}

test.describe('Overview Agent Work — grouped tool detail', () => {
  test('drill-down lazy-loads, groups by tool, and expands to per-call arguments', async ({ page }) => {
    const job = await pickJob(page);
    if (!job) { test.skip(true, 'No tasks on the board.'); return; }
    await pinEvidence(page, job.id);
    await openOverview(page, job);

    // The Agent Work section renders (summary pinned with tool calls > 0).
    const agentWork = page.getByTestId('overview-agent-work');
    await expect(agentWork).toBeVisible({ timeout: 15_000 });

    // Drill-down starts collapsed: the toggle is present, the body is not.
    const toggle = page.getByTestId('agent-work-detail-toggle');
    await expect(toggle).toBeVisible();
    await expect(page.getByTestId('agent-work-detail-body')).toHaveCount(0);

    // Expand: groups appear, one per tool, with the honest count.
    await toggle.click();
    const groups = page.getByTestId('agent-work-detail-group');
    await expect(groups.first()).toBeVisible({ timeout: 10_000 });
    await expect(groups).toHaveCount(5);
    await expect(groups.first()).toContainText('Bash');
    await expect(groups.first()).toContainText('68');

    // Expand the Bash group: its individual calls (the "what") show.
    await groups.first().getByRole('button').click();
    const calls = page.getByTestId('agent-work-detail-call');
    await expect(calls.first()).toBeVisible();
    await expect(calls.first()).toContainText('npm test');
    // The errored call carries the error styling.
    await expect(page.locator('.awd-call--error')).toContainText('dotnet test');

    await page.screenshot({
      path: path.join(SHOTS_DIR, 'agent-work-detail-expanded.png'),
      fullPage: false,
    });
  });
});
