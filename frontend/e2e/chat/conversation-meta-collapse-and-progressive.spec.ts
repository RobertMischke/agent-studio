import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { listJobs, type Job } from '../helpers/jobs';

/**
 * Meta-info collapse + progressive disclosure for the next-gen
 * conversation view (`Frontend:NextGenChat`). Operator request: lift
 * lifecycle noise (`Session init`, `Rate limit`) out of the visible flow
 * into a sidecar tooltip on the bubble header, keep same-actor messages
 * glued in one box even across long pauses, and gate long bursts behind
 * a "show N more" affordance with per-item expand for clamped bodies.
 *
 * Acceptance per the queued task
 * (`feature-conversation-view-meta-info-collapse-and-more-on-demand-progressive-disclosure`):
 *
 *  1. A burst of 12 task notifications + a session init + a rate-limit
 *     telemetry line collapses to ONE bubble — not 14 cards.
 *  2. The bubble's first 5 items render; "show N more" reveals the rest.
 *  3. Hovering the bubble header surfaces the full session id and the
 *     rate-limit telemetry as tooltip content.
 *  4. The full session id rides along on the bubble's `data-session-id`
 *     attribute so the projection can be verified without parsing the
 *     truncated chip.
 */

const RESULTS_DIR = path.resolve(
  __dirname,
  '../../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/feature-conversation-view-meta-info-collapse-and-more-on-demand-progressive-disclosure/results'
);

const MOUNTABLE_LANES = new Set(['3-progress', '4-auto-review', '5-human-review']);
const SESSION_ID = 'c705779a-a6bc-43ac-bada-358ea7e11a28';
const RATE_LIMIT_LINE =
  '● Rate limit · five-hour · allowed · reset in 4.4 h  [window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false]';

interface OutLine {
  timestamp: string;
  stream: string;
  text: string;
}

function buildOutputBuffer(): OutLine[] {
  const t0 = Date.now() - 15 * 60 * 1000;
  const t = (offsetSec: number) => new Date(t0 + offsetSec * 1000).toISOString();

  const lines: OutLine[] = [
    // Lifecycle noise the bubble should swallow into its meta tooltip.
    { timestamp: t(0), stream: 'stdout', text: `● Session init ${SESSION_ID}` },
    { timestamp: t(1), stream: 'stdout', text: RATE_LIMIT_LINE },
  ];

  // Twelve task_notification payloads. The "Session task_notification <id>"
  // prefix gets stripped so the bubble shows the payload itself, not the
  // bookkeeping. The payloads echo what the operator captured in the
  // 2026-05-28 screenshot.
  const payloads = [
    'total 340',
    'session-events.jsonl',
    'job.json',
    'human-decision-needed-bug-collapsed-lane-identity',
    'ls: cannot access tmp/foo: No such file or directory',
    'analyse-inline-meta-docs-coverage-audit',
    'reading prompt.md',
    'parsed run-context.json',
    'wrote results/coalesced-agent-bubbles.png',
    'tests: 312 passed, 0 failed',
    'Deploy to staging: queued',
    'Build succeeded in 12.4s',
  ];

  payloads.forEach((p, i) => {
    lines.push({
      timestamp: t(10 + i),
      stream: 'stdout',
      text: `● Session task_notification ${SESSION_ID} ${p}`,
    });
  });

  return lines;
}

async function pickMountableJob(): Promise<Job | null> {
  const jobs = await listJobs();
  const prog = jobs.find((j) => j.state === '3-progress');
  if (prog) return prog;
  return jobs.find((j) => MOUNTABLE_LANES.has(j.state)) ?? null;
}

async function installOutputMock(page: Page, jobId: string): Promise<void> {
  const outputBody = JSON.stringify(buildOutputBuffer());
  const escId = encodeURIComponent(jobId);
  await page.route(`**/api/tasks/${escId}/output?**`, async (route) => {
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

test.describe('Conversation view collapses meta + progressively discloses items', () => {
  test('Session init + Rate limit + 12 task notifications collapse to one bubble with 5 items + show-more', async ({
    page,
  }) => {
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

    // Exactly one agent bubble — not 14.
    const agentBubbles = conv.locator('[data-testid="conversation-message-message.taskAgent"]');
    await expect(agentBubbles).toHaveCount(1);

    // The visible items list shows 5; the remaining 7 hide behind show-more.
    const items = agentBubbles.first().locator('[data-testid="conversation-message-item"]');
    await expect(items).toHaveCount(5);

    const showMore = agentBubbles.first().locator('[data-testid="conversation-message-show-more"]');
    await expect(showMore).toBeVisible();
    await expect(showMore).toContainText('7');

    // The full session id rides along on the bubble. Rate-limit indicator visible.
    await expect(agentBubbles.first()).toHaveAttribute('data-session-id', SESSION_ID);
    await expect(agentBubbles.first()).toHaveAttribute('data-has-rate-limit', 'true');

    // Each item body is the payload, not the bookkeeping prefix.
    const firstItemText = await items.first().innerText();
    expect(firstItemText).toContain('total 340');
    expect(firstItemText).not.toContain('Session task_notification');
    expect(firstItemText).not.toContain(SESSION_ID);

    // No item exposes the raw Session init / Rate limit lines.
    const allItemText = await items.allInnerTexts();
    for (const text of allItemText) {
      expect(text).not.toContain('Session init');
      expect(text).not.toContain('● Rate limit');
    }

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'meta-collapsed-progressive.png'),
      fullPage: false,
    });

    // Clicking "show 7 more" reveals all 12.
    await showMore.click();
    await expect(items).toHaveCount(12);
    await expect(
      agentBubbles.first().locator('[data-testid="conversation-message-show-more"]')
    ).toHaveCount(0);
    await expect(
      agentBubbles.first().locator('[data-testid="conversation-message-show-less"]')
    ).toBeVisible();

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'meta-expanded-all-items.png'),
      fullPage: false,
    });
  });

  test('the bubble header carries the session id and rate-limit telemetry on hover', async ({
    page,
  }) => {
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

    const bubble = conv.locator('[data-testid="conversation-message-message.taskAgent"]').first();
    const head = bubble.locator('[data-testid="conversation-message-head"]');
    await expect(head).toBeVisible();

    // Hover the head; the canonical tooltip surface renders the full session
    // id and the rate-limit telemetry. The tooltip lives at the page level,
    // not inside the bubble, so we query the document root.
    await head.hover();
    const tooltip = page.locator('.cac-tooltip[data-placement]');
    await expect(tooltip).toBeVisible({ timeout: 5_000 });
    const tooltipText = await tooltip.innerText();
    expect(tooltipText).toContain(SESSION_ID);
    expect(tooltipText.toLowerCase()).toContain('rate limit');

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'meta-tooltip-hover.png'),
      fullPage: false,
    });
  });
});
