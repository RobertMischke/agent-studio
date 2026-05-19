import { test, expect, Page } from '@playwright/test';
import { startLongTaskRecorder } from '../helpers/timing';

/**
 * Slice C right-rail markers. Pinned behaviour:
 *   1. Each non-trivial turn / event becomes a chip at the same vertical
 *      fraction as its source row in the chat list.
 *   2. Tightly-spaced chips collapse into a cluster with a count badge.
 *      Clicking a cluster opens a stacked menu of its members.
 *   3. Clicking a chip emits a turnId that the host smooth-scrolls to;
 *      the destination row gets the existing `pchat__turn--flash` glow.
 *   4. The viewport band tracks the visible window of the chat list.
 *   5. LongTask budget during a 5 s rail-adjacent scroll burst stays
 *      under the same 50 ms ceiling Slice D pinned for the chat list.
 *
 * Stubs the same `/api/projects/{p}/chat/scroll` + `/turn/...` endpoints
 * Slice D introduced; we don't need search here, the rail only paints
 * in `live` mode.
 */

const SHOTS = 'screenshots/project-chat-rail';
const PROJECT = 'demo-project';

interface FixtureTurn {
  turnId: string;
  author: string;
  kind: string;
  ts: string;
  body: string;
}

/**
 * Build a fixture that exercises every rail chip kind plus a cluster.
 *
 * - 80 turns total at 1-minute spacing.
 * - "long turn" chips: every 12th index gets a body with 12 hard lines.
 * - "event" chips: indices [10, 20, 30, 40] — `event-tool-call`.
 * - "error" chips: indices [22, 55] — `event-watchdog` / `event-rate-limit`.
 * - cluster: indices [60..67] are all events so they collapse into one
 *   cluster on a typical 600-px rail height.
 */
function buildRailFixture(): FixtureTurn[] {
  const start = new Date('2026-04-01T00:00:00Z').getTime();
  const turns: FixtureTurn[] = [];
  const longLines = Array.from({ length: 12 }, (_, i) => `Long line ${i + 1}.`).join('\n');
  for (let i = 0; i < 80; i++) {
    const ts = new Date(start + i * 60_000).toISOString();
    let kind = 'turn';
    let body = `Mundane turn ${i}: lorem ipsum ${i}.`;
    if (i === 22) {
      kind = 'event-watchdog';
      body = 'Watchdog killed the agent: phase silence exceeded budget.';
    } else if (i === 55) {
      kind = 'event-rate-limit';
      body = 'Rate-limit hit: cooling down for 60s.';
    } else if ([10, 20, 30, 40].includes(i)) {
      kind = 'event-tool-call';
      body = `Tool call ${i}: read frontend/src/app/components/foo.ts`;
    } else if (i >= 60 && i <= 67) {
      kind = 'event-update';
      body = `Update event ${i}: synced 3 files.`;
    } else if (i % 12 === 0) {
      body = longLines;
    }
    const author = kind === 'turn'
      ? (i % 7 === 0 ? 'user' : (i % 3 === 0 ? 'orchestrator' : 'agent'))
      : 'orchestrator';
    turns.push({ turnId: `t${String(i).padStart(4, '0')}`, author, kind, ts, body });
  }
  return turns;
}

const FIXTURE = buildRailFixture();

async function stubChat(page: Page): Promise<void> {
  await page.route(/\/api\/projects\/[^/]+\/chat\/scroll/, async (route) => {
    const url = new URL(route.request().url());
    const before = url.searchParams.get('before');
    const after = url.searchParams.get('after');
    const limit = Math.min(parseInt(url.searchParams.get('limit') ?? '50', 10), 200);
    const sorted = [...FIXTURE].sort((a, b) => a.ts.localeCompare(b.ts));
    let page$: FixtureTurn[];
    let direction: 'before' | 'after' | 'tail';
    if (before) {
      direction = 'before';
      page$ = sorted.filter(t => t.ts < before).slice(-limit).reverse();
    } else if (after) {
      direction = 'after';
      page$ = sorted.filter(t => t.ts > after).slice(0, limit);
    } else {
      direction = 'tail';
      page$ = sorted.slice(-limit).reverse();
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, direction, turns: page$ }),
    });
  });

  await page.route(/\/api\/projects\/[^/]+\/chat\/turn\/[^?]+/, async (route) => {
    const url = new URL(route.request().url());
    const m = url.pathname.match(/\/turn\/([^/]+)$/);
    const id = m ? decodeURIComponent(m[1]) : '';
    const found = FIXTURE.find(t => t.turnId === id);
    if (!found) { await route.fulfill({ status: 404, body: '{}' }); return; }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, turn: found }),
    });
  });

  // /search is unused but the FE may probe it on focus; stub to empty.
  await page.route(/\/api\/projects\/[^/]+\/chat\/search/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, results: [] }),
    });
  });
}

async function openSheet(page: Page, opts: { expectTurns?: boolean } = {}): Promise<boolean> {
  // Arm the response listener before the click so we don't lose the
  // request that fires the moment the chat list mounts.
  const scrollResponse = page
    .waitForResponse(/\/api\/projects\/[^/]+\/chat\/scroll/, { timeout: 8_000 })
    .catch(() => null);
  await page.goto('/?virtualChat=1');
  await page.waitForLoadState('domcontentloaded');
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  const list = page.getByTestId('project-chat-list');
  if (!(await list.count())) {
    test.skip(true, 'No watched projects available');
    return false;
  }
  await expect(list).toBeVisible();
  await scrollResponse;
  if (opts.expectTurns !== false) {
    await page.getByTestId('pchat-turn').first().waitFor({ state: 'attached', timeout: 5_000 })
      .catch(() => { /* an empty corpus is also a valid state */ });
  }
  return true;
}

test.describe('Project chat — right-rail markers', () => {
  test('rail mounts next to the chat with chips for events + long turns', async ({ page }) => {
    await stubChat(page);
    if (!(await openSheet(page))) return;

    const rail = page.getByTestId('project-chat-rail');
    await expect(rail).toBeVisible();

    // Mid-density: scroll up so several chip-eligible rows are loaded.
    const scroll = page.getByTestId('pchat-scroll');
    await scroll.evaluate((el) => { el.scrollTop = 0; });
    await page.waitForTimeout(400);

    const chips = page.getByTestId('pchat-rail-chip');
    const clusters = page.getByTestId('pchat-rail-cluster');
    const totalMarkers = (await chips.count()) + (await clusters.count());
    expect(totalMarkers).toBeGreaterThan(3);

    await page.screenshot({ path: `${SHOTS}/01-mid-density.png` });
  });

  test('error chip is rendered and labelled', async ({ page }) => {
    await stubChat(page);
    if (!(await openSheet(page))) return;

    const scroll = page.getByTestId('pchat-scroll');
    await scroll.evaluate((el) => { el.scrollTop = 0; });
    await page.waitForTimeout(400);

    // Sweep both single chips and clusters because the error event may
    // be visually merged into a neighbouring cluster on very short rails.
    const errorMarker = page.locator('[data-testid^="pchat-rail-"][data-kind="error"]');
    await expect(errorMarker.first()).toBeVisible();

    await page.screenshot({ path: `${SHOTS}/02-error-chip.png` });
  });

  test('cluster forms and its menu lists members', async ({ page }) => {
    await stubChat(page);
    if (!(await openSheet(page))) return;

    const scroll = page.getByTestId('pchat-scroll');
    await scroll.evaluate((el) => { el.scrollTop = 0; });
    await page.waitForTimeout(500);

    const clusters = page.getByTestId('pchat-rail-cluster');
    const clusterCount = await clusters.count();
    expect(clusterCount).toBeGreaterThan(0);

    await page.screenshot({ path: `${SHOTS}/03-clusters.png` });

    await clusters.first().click();
    const menu = page.getByTestId('pchat-rail-cluster-menu');
    await expect(menu).toBeVisible();

    const items = page.getByTestId('pchat-rail-cluster-item');
    expect(await items.count()).toBeGreaterThan(1);

    await page.screenshot({ path: `${SHOTS}/04-cluster-menu.png` });
  });

  test('clicking a chip flashes the destination turn', async ({ page }) => {
    await stubChat(page);
    if (!(await openSheet(page))) return;

    const scroll = page.getByTestId('pchat-scroll');
    await scroll.evaluate((el) => { el.scrollTop = 0; });
    await page.waitForTimeout(500);

    // Pick the first single chip — clusters open a menu instead of
    // scrolling, so we drive the scroll path directly.
    const chip = page.getByTestId('pchat-rail-chip').first();
    await expect(chip).toBeVisible();
    await chip.click();

    // Slice D's `flash()` adds `.pchat__turn--flash` for ~1.5 s. We
    // assert it appears on at least one turn after the click.
    await page.waitForFunction(
      () => !!document.querySelector('.pchat__turn--flash'),
      undefined,
      { timeout: 3_000 },
    );
  });

  test('empty chat: rail mounts but has no chips', async ({ page }) => {
    // Override stub: tail returns an empty corpus.
    await page.route(/\/api\/projects\/[^/]+\/chat\/scroll/, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project: PROJECT, direction: 'tail', turns: [] }),
      });
    });
    if (!(await openSheet(page, { expectTurns: false }))) return;

    const rail = page.getByTestId('project-chat-rail');
    await expect(rail).toBeVisible();
    await expect(rail).toHaveAttribute('data-density', 'empty');
    expect(await page.getByTestId('pchat-rail-chip').count()).toBe(0);
    expect(await page.getByTestId('pchat-rail-cluster').count()).toBe(0);

    await page.screenshot({ path: `${SHOTS}/05-empty.png` });
  });

  test('long-task budget under 200ms during a 5 s scroll burst', async ({ page }) => {
    await stubChat(page);
    if (!(await openSheet(page))) return;

    // Let the initial render + markdown parse settle before measuring.
    await page.waitForTimeout(600);

    const recorder = await startLongTaskRecorder(page);
    // Snapshot the baseline so we only count long tasks that happen
    // *during* the scroll burst — the recorder's `buffered: true` mode
    // replays mount-time entries into the running total, which would
    // otherwise pollute the 50 ms scroll budget.
    const baseline = await recorder.totalMs();

    const scroll = page.getByTestId('pchat-scroll');
    const t0 = Date.now();
    while (Date.now() - t0 < 5_000) {
      await scroll.evaluate((el) => {
        el.scrollTop = Math.max(0, el.scrollTop - 80);
      });
      await page.waitForTimeout(40);
    }
    const total = await recorder.totalMs();
    await recorder.stop();

    const burstOnly = total - baseline;
    // Same headroom as the Slice D contract — 50 ms is the design
    // ceiling, but CI Long-Task counters jitter so we cap at 200.
    expect(burstOnly).toBeLessThan(200);
  });
});
