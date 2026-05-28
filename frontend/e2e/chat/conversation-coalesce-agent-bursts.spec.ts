import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { listJobs, type Job } from '../helpers/jobs';

/**
 * Coalescing regression for the next-gen conversation view
 * (`Frontend:NextGenChat`). When the agent emits several short
 * notifications in a row, the renderer must fold them into one bubble
 * with a compact `<li>` list — not paint a wall of bordered AGENT
 * cards. A user turn between two agent runs breaks the coalesce so
 * the chronology stays readable.
 *
 * Acceptance per the queued task
 * (`feature-conversation-view-coalesce-agent-session-events-into-compact-list`):
 *
 *  1. Many consecutive agent notifications fold into ONE bubble whose
 *     body lists the notifications as `<li>`s.
 *  2. A USER follow-up forces a break: a fresh bubble starts after it.
 *  3. The visible DOM never carries a "task_started" / runMarker.start
 *     row — the bubble header already communicates "agent active at
 *     this time", so the marker is filtered out.
 *
 * The spec attaches to any task whose detail pane is mountable (a job in
 * 3-progress, 4-auto-review, or 5-human-review) and routes only the
 * `/output` endpoint to a deterministic fixture buffer. The job's real
 * detail / runs / status responses continue to flow from the backend so
 * the protocol pane mounts normally; only the cli-output is replaced so
 * the coalescing logic runs on a predictable burst pattern.
 */

const RESULTS_DIR = path.resolve(
  __dirname,
  '../../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/feature-conversation-view-coalesce-agent-session-events-into-compact-list/results'
);

const MOUNTABLE_LANES = new Set(['3-progress', '4-auto-review', '5-human-review']);

interface OutLine {
  timestamp: string;
  stream: string;
  text: string;
}

function buildOutputBuffer(): OutLine[] {
  const t0 = Date.now() - 10 * 60 * 1000;
  const t = (offsetSec: number) => new Date(t0 + offsetSec * 1000).toISOString();
  // Nine consecutive agent notifications, then a user follow-up, then three
  // more agent notifications. Plain text lines (no leading "*" or "x")
  // become message.taskAgent events in the projection.
  return [
    { timestamp: t(0),  stream: 'stdout', text: 'On branch main' },
    { timestamp: t(1),  stream: 'stdout', text: 'Bash completed with no output' },
    { timestamp: t(2),  stream: 'stdout', text: '4b02f9c fix(board): replace single MANUAL pill with In-Progress lane status cluster' },
    { timestamp: t(3),  stream: 'stdout', text: '[main 579ba96] docs(orchestrator-steering): document STEER reply contract in AGENTS.md' },
    { timestamp: t(4),  stream: 'stdout', text: '579ba96 docs(orchestrator-steering): document STEER reply contract in AGENTS.md' },
    { timestamp: t(5),  stream: 'stdout', text: 'Pushed origin/main' },
    { timestamp: t(6),  stream: 'stdout', text: 'Lint clean' },
    { timestamp: t(7),  stream: 'stdout', text: 'Build succeeded' },
    { timestamp: t(8),  stream: 'stdout', text: 'All tests passed' },
    { timestamp: t(9),  stream: 'user',   text: 'Now run the deploy script.' },
    { timestamp: t(10), stream: 'stdout', text: 'Deploying to staging' },
    { timestamp: t(11), stream: 'stdout', text: 'Deploy completed in 12s' },
    { timestamp: t(12), stream: 'stdout', text: 'Healthcheck OK' },
  ];
}

async function pickMountableJob(): Promise<Job | null> {
  const jobs = await listJobs();
  // Prefer 3-progress (the live lane) so the Activity tab is the default,
  // but accept review lanes too — both render the protocol pane.
  const prog = jobs.find((j) => j.state === '3-progress');
  if (prog) return prog;
  return jobs.find((j) => MOUNTABLE_LANES.has(j.state)) ?? null;
}

async function installOutputMock(page: Page, jobId: string): Promise<void> {
  const outputBody = JSON.stringify(buildOutputBuffer());
  const escId = encodeURIComponent(jobId);
  // Only route the output endpoint — the job detail / runs / status etc.
  // come from the real backend so the protocol pane mounts normally.
  await page.route(`**/api/jobs/${escId}/output?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: outputBody });
  });
}

async function setNextGenChatFlag(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.nextGenChat', '1');
  });
}

async function openActivityTab(page: Page, job: Job): Promise<void> {
  await installOutputMock(page, job.id);
  await page.goto(
    `/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`
  );
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 15_000 });
  await activityTab.click();
}

test.describe('Conversation view coalesces consecutive agent bursts', () => {
  test('nine agent notifications + user + three more render as two bubbles, not twelve', async ({ page }) => {
    const job = await pickMountableJob();
    if (!job) {
      test.skip(true, 'No mountable job (3-progress / 4-auto-review / 5-human-review) on the board.');
      return;
    }
    await setNextGenChatFlag(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await openActivityTab(page, job);

    const conv = page.getByTestId('conversation-view');
    await expect(conv).toBeVisible({ timeout: 10_000 });

    // Two AGENT bubbles (before + after the user), one USER bubble — not 12.
    const agentBubbles = conv.locator('[data-testid="conversation-message-message.taskAgent"]');
    const userBubbles = conv.locator('[data-testid="conversation-message-message.user"]');
    await expect(agentBubbles).toHaveCount(2);
    await expect(userBubbles).toHaveCount(1);

    // Progressive disclosure: the first agent bubble has 9 items folded into
    // one list, but only the first 5 render until the user expands. The
    // second bubble's 3 items fit under the limit so it shows all of them.
    const firstAgentItems = agentBubbles.first().locator('[data-testid="conversation-message-item"]');
    const secondAgentItems = agentBubbles.nth(1).locator('[data-testid="conversation-message-item"]');
    await expect(firstAgentItems).toHaveCount(5);
    await expect(secondAgentItems).toHaveCount(3);

    // The "N events" badge surfaces the coalesce on the multi-item bubble —
    // it counts the underlying coalesce total, not the visible-items count.
    await expect(agentBubbles.first().locator('[data-testid="conversation-message-count"]'))
      .toContainText('9 events');

    // "show 4 more" expands to all 9 items.
    const showMore = agentBubbles.first().locator('[data-testid="conversation-message-show-more"]');
    await expect(showMore).toBeVisible();
    await expect(showMore).toContainText('4');
    await showMore.click();
    await expect(firstAgentItems).toHaveCount(9);

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'coalesced-agent-bubbles.png'),
      fullPage: false,
    });
  });

  test('the visible feed never shows a runMarker start row', async ({ page }) => {
    const job = await pickMountableJob();
    if (!job) {
      test.skip(true, 'No mountable job on the board.');
      return;
    }
    await setNextGenChatFlag(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await openActivityTab(page, job);

    const conv = page.getByTestId('conversation-view');
    await expect(conv).toBeVisible({ timeout: 10_000 });

    // Any runMarker that does render must NOT be the "start" marker —
    // it is filtered out as redundant with the bubble header.
    const startMarkers = conv.locator('[data-testid="conversation-run-marker"][data-marker="start"]');
    await expect(startMarkers).toHaveCount(0);
  });
});
