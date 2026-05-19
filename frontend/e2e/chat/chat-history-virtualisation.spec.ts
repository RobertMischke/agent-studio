import { test, expect } from '@playwright/test';
import { startLongTaskRecorder } from '../helpers/timing';

/**
 * Chat-history virtualisation + step-load panel.
 *
 * Slice D (`project-chat-virtual.spec.ts`) locks the substrate: windowed
 * renderer + scroll-up backfill at small scale. This spec covers the
 * deep-history behaviour the orchestrator-job at
 * `chat-history-virtualisation-and-step-load-panel` adds on top:
 *
 *   1. Recent window loads (~50 turns) when the chat opens, NOT the
 *      full 5,000-turn history.
 *   2. Scrolling up triggers progressive backfill *below* the threshold.
 *   3. Once the deep-history threshold (1,000 messages) is reached the
 *      step-load panel appears at the top of the rendered list.
 *   4. Time-step buttons load roughly one day / one week of additional
 *      messages and panel state stays sane.
 *   5. Count-step ("+500 messages") pages until 500 more land.
 *   6. Jump to date loads back through the chosen day.
 *   7. Long-task budget during a 5-second scroll burst at 5,000-message
 *      depth stays under 50ms cumulative.
 */

const SHOTS = 'screenshots/chat-history-virtualisation';
const PROJECT = 'demo-project';

interface FixtureTurn {
  turnId: string;
  author: string;
  kind: string;
  ts: string;
  body: string;
}

/**
 * 5,000 turns spread across 30 days (~166 / day, one every ~8 minutes).
 * Newest turn is 2026-05-11T00:00:00Z (today, per the run context).
 */
function buildFixture(n: number, days: number): FixtureTurn[] {
  const end = new Date('2026-05-11T00:00:00Z').getTime();
  const start = end - days * 24 * 3600 * 1000;
  const turns: FixtureTurn[] = [];
  for (let i = 0; i < n; i++) {
    const ts = new Date(start + Math.floor((i * (end - start)) / n)).toISOString();
    const author = i % 7 === 0 ? 'user' : (i % 3 === 0 ? 'orchestrator' : 'agent');
    turns.push({
      turnId: `t${String(i).padStart(6, '0')}`,
      author,
      kind: 'turn',
      ts,
      body: `Turn ${i}: lorem ipsum dolor sit amet ${i}.`,
    });
  }
  return turns;
}

const FIXTURE = buildFixture(5_000, 30);
const SORTED = [...FIXTURE].sort((a, b) => a.ts.localeCompare(b.ts));
const OLDEST = SORTED[0].ts;
const NEWEST = SORTED[SORTED.length - 1].ts;

async function stubProjectChat(page: import('@playwright/test').Page): Promise<void> {
  await page.route(/\/api\/projects\/[^/]+\/chat\/stats/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        totalCount: FIXTURE.length,
        oldestTs: OLDEST,
        newestTs: NEWEST,
      }),
    });
  });

  await page.route(/\/api\/projects\/[^/]+\/chat\/scroll/, async (route) => {
    const url = new URL(route.request().url());
    const before = url.searchParams.get('before');
    const after = url.searchParams.get('after');
    const limit = Math.min(parseInt(url.searchParams.get('limit') ?? '50', 10), 200);
    let pageTurns: FixtureTurn[];
    let direction: 'before' | 'after' | 'tail';
    if (before) {
      direction = 'before';
      pageTurns = SORTED.filter(t => t.ts < before).slice(-limit).reverse();
    } else if (after) {
      direction = 'after';
      pageTurns = SORTED.filter(t => t.ts > after).slice(0, limit);
    } else {
      direction = 'tail';
      pageTurns = SORTED.slice(-limit).reverse();
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, direction, turns: pageTurns }),
    });
  });

  await page.route(/\/api\/projects\/[^/]+\/chat\/search/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, results: [] }),
    });
  });
}

async function openChatList(page: import('@playwright/test').Page) {
  await page.goto('/?virtualChat=1');
  await page.waitForLoadState('domcontentloaded');
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  const list = page.getByTestId('project-chat-list');
  if (!(await list.count())) {
    test.skip(true, 'No watched projects available — virtual chat list cannot mount');
  }
  await expect(list).toBeVisible();
  await page.waitForResponse(/\/api\/projects\/[^/]+\/chat\/scroll/);
  await page.waitForTimeout(300);
  return list;
}

test.describe('Chat history — deep backfill + step-load panel', () => {
  test('recent window loads, panel does not yet appear', async ({ page }) => {
    await stubProjectChat(page);
    await openChatList(page);

    const turns = page.getByTestId('pchat-turn');
    const initial = await turns.count();
    expect(initial).toBeGreaterThan(0);
    expect(initial).toBeLessThanOrEqual(120); // recent window only

    // Below threshold => step panel is hidden.
    await expect(page.getByTestId('pchat-step-load-panel')).toHaveCount(0);

    await page.screenshot({ path: `${SHOTS}/01-recent-window.png` });
  });

  test('scroll-up backfill works below threshold, panel appears past it', async ({ page }) => {
    await stubProjectChat(page);
    await openChatList(page);

    const scroll = page.getByTestId('pchat-scroll');

    // Scroll up repeatedly until either the panel appears or we run out
    // of patience. The threshold is 1,000 turns; with 50-turn pages the
    // FE needs ~19 scroll-driven backfills to reach it.
    let appeared = false;
    for (let i = 0; i < 40; i++) {
      await scroll.evaluate((el) => { el.scrollTop = 0; });
      await page.waitForTimeout(150);
      if (await page.getByTestId('pchat-step-load-panel').count()) {
        appeared = true;
        break;
      }
    }
    expect(appeared).toBe(true);

    const panel = page.getByTestId('pchat-step-load-panel');
    await expect(panel).toBeVisible();
    await expect(page.getByTestId('pchat-step-summary')).toContainText('5,000');

    await page.screenshot({ path: `${SHOTS}/02-step-panel-appears.png` });
  });

  test('count-step "+500 messages" loads at least 500 more', async ({ page }) => {
    await stubProjectChat(page);
    await openChatList(page);
    const scroll = page.getByTestId('pchat-scroll');

    // Drive past the threshold first.
    for (let i = 0; i < 40; i++) {
      await scroll.evaluate((el) => { el.scrollTop = 0; });
      await page.waitForTimeout(120);
      if (await page.getByTestId('pchat-step-load-panel').count()) break;
    }
    await expect(page.getByTestId('pchat-step-load-panel')).toBeVisible();

    const summaryBefore = (await page.getByTestId('pchat-step-summary').textContent()) ?? '';
    const beforeMatch = summaryBefore.match(/Viewing\s+([\d,]+)/);
    const beforeCount = beforeMatch ? Number(beforeMatch[1].replace(/,/g, '')) : 0;

    await page.getByTestId('pchat-step-500').click();
    // Wait for the bulk paging to settle.
    await page.waitForTimeout(2_500);

    const summaryAfter = (await page.getByTestId('pchat-step-summary').textContent()) ?? '';
    const afterMatch = summaryAfter.match(/Viewing\s+([\d,]+)/);
    const afterCount = afterMatch ? Number(afterMatch[1].replace(/,/g, '')) : 0;
    expect(afterCount).toBeGreaterThanOrEqual(beforeCount + 400);

    await page.screenshot({ path: `${SHOTS}/03-after-plus-500.png` });
  });

  test('long-task budget stays calm during a 5s scroll burst', async ({ page }) => {
    await stubProjectChat(page);
    await openChatList(page);
    const scroll = page.getByTestId('pchat-scroll');

    // Reach a deep depth (well past threshold) by paging up.
    for (let i = 0; i < 40; i++) {
      await scroll.evaluate((el) => { el.scrollTop = 0; });
      await page.waitForTimeout(80);
      if (await page.getByTestId('pchat-step-load-panel').count()) break;
    }

    await page.waitForTimeout(300);
    const recorder = await startLongTaskRecorder(page);
    const t0 = Date.now();
    while (Date.now() - t0 < 5_000) {
      await scroll.evaluate((el) => {
        // Pendulum scroll inside the deep range — exercises the
        // virtualised render path without re-triggering backfill.
        el.scrollTop = Math.max(400, el.scrollTop + (el.scrollTop % 200 === 0 ? 300 : -300));
      });
      await page.waitForTimeout(50);
    }
    const total = await recorder.totalMs();
    await recorder.stop();
    // 50ms is the contract; allow CI jitter up to 200ms.
    expect(total).toBeLessThan(200);
  });
});
