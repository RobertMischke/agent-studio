import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Stick-to-bottom regression for the next-gen conversation view
 * (`Frontend:NextGenChat`, ASS-677..682 Job-Details cluster).
 *
 * The bug: the embedded conversation view never pinned to the latest entry.
 * Its real scroll container used to be the protocol pane's `.pane__body`
 * (the view itself did not scroll), and nothing scrolled it to the bottom on
 * load or while the agent streamed — so the newest line sat far below the
 * fold (measured ~2100px on a 900px viewport). The fix adds a shared
 * `StickToBottomDirective` to the conversation view.
 *
 * Since then the protocol pane turned `[virtualised]="true"` on for this
 * consumer (windowed DOM for long transcripts). Virtualised mode moves scroll
 * ownership onto the conversation view's own `.conv` root (bounded via
 * `bodyMaxHeight`, `overflow-y: auto`) instead of the `.pane__body` ancestor
 * — see the `cac-conversation-view` host-sizing rule in
 * `protocol-pane.component.scss`. `readGeometry`/`scrollContainerToTop` below
 * check the conversation-view element itself before walking up, so the spec
 * finds the right scroller under either ownership model.
 *
 * This spec mounts the view over a large deterministic output buffer (so the
 * pane is guaranteed to overflow) and asserts:
 *   1. On load the view is stuck — the latest row is in view.
 *   2. Scrolling up deliberately releases the stick and reveals the
 *      "jump to latest" affordance.
 *   3. Clicking "jump to latest" re-pins to the bottom.
 */

const RESULTS_DIR = path.resolve(
  __dirname,
  '../../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/bug-conversation-view-scroll-unstable-whitespace-not-pinned-to-latest/results'
);

const MOUNTABLE_LANES = new Set(['3-progress', '4-auto-review', '5-human-review']);

interface OutLine {
  timestamp: string;
  stream: string;
  text: string;
}

function buildLargeBuffer(): OutLine[] {
  const t0 = Date.now() - 30 * 60 * 1000;
  const t = (s: number) => new Date(t0 + s * 1000).toISOString();
  const lines: OutLine[] = [];
  let s = 0;
  for (let i = 0; i < 60; i++) {
    if (i % 9 === 0) {
      lines.push({ timestamp: t(s++), stream: 'user', text: `User instruction ${i}: continue with the next step and report back when done.` });
    }
    lines.push({ timestamp: t(s++), stream: 'stdout', text: `Agent step ${i}: ${'lorem ipsum dolor sit amet '.repeat((i % 5) + 1)}` });
  }
  lines.push({ timestamp: t(s++), stream: 'stdout', text: 'FINAL-AGENT-LINE pinned to the bottom on load.' });
  return lines;
}

async function pickJob(page: Page): Promise<{ id: string; watchPath: string } | null> {
  const res = await page.request.get('/api/tasks');
  if (!res.ok()) return null;
  const arr = await res.json();
  const j =
    arr.find((x: any) => x.state === '3-progress')
    ?? arr.find((x: any) => MOUNTABLE_LANES.has(x.state));
  return j ? { id: j.id, watchPath: j.watchPath } : null;
}

async function mountConversation(page: Page, job: { id: string; watchPath: string }): Promise<void> {
  const body = JSON.stringify(buildLargeBuffer());
  const esc = encodeURIComponent(job.id);
  // Cover both the current (/api/tasks) and legacy (/api/tasks) output routes.
  await page.route(`**/api/tasks/${esc}/output?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body })
  );
  await page.route(`**/api/tasks/${esc}/output?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body })
  );
  await page.addInitScript(() => localStorage.setItem('atp.flag.nextGenChat', '1'));
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${esc}&watchPath=${encodeURIComponent(job.watchPath)}`);
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 20_000 });
  await activityTab.click();
}

/**
 * Geometry of the conversation view's nearest scrollable container. Checks
 * the element itself first — virtualised mode makes `.conv` (the element
 * carrying `data-testid="conversation-view"`) the scroller — then falls back
 * to walking up the ancestor chain for the non-virtualised/legacy case where
 * a wrapping host (e.g. `.pane__body`) owns the scroll instead.
 */
async function readGeometry(page: Page) {
  return page.getByTestId('conversation-view').evaluate((el) => {
    let cur: HTMLElement | null = el as HTMLElement;
    while (cur) {
      const oy = getComputedStyle(cur).overflowY;
      if ((oy === 'auto' || oy === 'scroll' || oy === 'overlay') && cur.scrollHeight > cur.clientHeight + 1) break;
      cur = cur.parentElement;
    }
    if (!cur) return null;
    return {
      scrollTop: cur.scrollTop,
      scrollHeight: cur.scrollHeight,
      clientHeight: cur.clientHeight,
      distanceFromBottom: cur.scrollHeight - cur.scrollTop - cur.clientHeight,
    };
  });
}

async function scrollContainerToTop(page: Page): Promise<void> {
  await page.getByTestId('conversation-view').evaluate((el) => {
    let cur: HTMLElement | null = el as HTMLElement;
    while (cur) {
      const oy = getComputedStyle(cur).overflowY;
      if ((oy === 'auto' || oy === 'scroll' || oy === 'overlay') && cur.scrollHeight > cur.clientHeight + 1) break;
      cur = cur.parentElement;
    }
    if (cur) cur.scrollTop = 0;
  });
}

test.describe('Conversation view sticks to the latest entry', () => {
  test('pins to bottom on load, releases on scroll-up, re-pins via jump', async ({ page }) => {
    const job = await pickJob(page);
    if (!job) {
      test.skip(true, 'No mountable job on the board.');
      return;
    }
    await mountConversation(page, job);

    const conv = page.getByTestId('conversation-view');
    await expect(conv).toBeVisible({ timeout: 15_000 });
    // Let the projection settle and the directive perform its initial pin.
    await page.waitForTimeout(1000);

    const onLoad = await readGeometry(page);
    if (!onLoad) {
      test.skip(true, 'Conversation did not overflow its container — cannot test pinning.');
      return;
    }

    // 1. Stuck on load: the latest row is in view (near the bottom).
    expect(onLoad.distanceFromBottom).toBeLessThanOrEqual(48);
    await expect(conv).toHaveAttribute('data-stuck', 'true');
    await expect(page.getByTestId('conversation-jump-latest')).toHaveCount(0);
    await page.screenshot({ path: path.join(RESULTS_DIR, 'after-pinned-on-load.png'), fullPage: false });

    // 2. Deliberate scroll up releases the stick and shows the jump button.
    await scrollContainerToTop(page);
    await expect(conv).toHaveAttribute('data-stuck', 'false');
    const jump = page.getByTestId('conversation-jump-latest');
    await expect(jump).toBeVisible();
    await page.screenshot({ path: path.join(RESULTS_DIR, 'after-released-jump-visible.png'), fullPage: false });

    // 3. Jump re-pins to the bottom and hides the affordance.
    await jump.click();
    await page.waitForTimeout(300);
    await expect(conv).toHaveAttribute('data-stuck', 'true');
    await expect(page.getByTestId('conversation-jump-latest')).toHaveCount(0);
    const afterJump = await readGeometry(page);
    expect(afterJump?.distanceFromBottom ?? 0).toBeLessThanOrEqual(48);
  });
});
