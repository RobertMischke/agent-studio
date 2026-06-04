import { expect, test, type Page } from '@playwright/test';

/**
 * End-to-end acceptance: orchestrator-chat content is visible immediately
 * after load and stays visible when a scroll fires while sticky at the
 * bottom — no blank area, no manual scroll (ASS-665, ASS-613 sibling).
 *
 * The default orchestrator side sheet renders the conversation through
 * `<app-chat [virtualised]="true">`. The virtual window is seeded sticky-to-
 * bottom on load, but `onBodyScroll` used to re-derive the window from the
 * fixed row-height estimate (120px) on *every* scroll event — including the
 * ones fired while still pinned at the bottom (scroll-anchoring reflow during
 * the side-sheet open animation, async markdown growth, or the programmatic
 * pin's own event). Short orchestrator turns are far shorter than 120px, so
 * that estimate placed a phantom bottom spacer under the freshly loaded tail
 * and pushed it out of the viewport: blank area until a manual scroll.
 *
 * This spec stubs a deep history of short turns, opens the chat, fires a
 * scroll while sticky at the bottom, and asserts the newest turn stays
 * rendered + on-screen with no `chat-spacer-bottom`.
 *
 * NOTE ON COVERAGE: the deterministic fail-before/pass-after guard for this
 * fix lives in the unit spec (chat.component.spec.ts → "keeps the newest
 * turn rendered when a scroll fires while sticky-at-bottom"), which freezes
 * the exact sticky-bottom geometry that triggers the window-shrink. This e2e
 * exercises the real side-sheet open path in a browser, but the broken
 * symptom is a sub-frame virtual-window race whose reproduction depends on
 * open-animation reflow timing, so it is best-effort at catching the
 * regression and reliable as an acceptance check on the fixed build.
 */

const SHOTS = 'screenshots/orchestrator-chat-content-visible';
// Alpha-only: orchestrator turns render through markdown, and underscores
// would be eaten as emphasis (NEWEST_X_Y -> italic, underscores stripped).
const NEWEST_MARKER = 'ZZZNEWESTTURNVISIBLEZZZ';

interface StubTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
}

function buildTurns(n: number): StubTurn[] {
  const base = Date.parse('2026-06-04T09:00:00Z');
  const turns: StubTurn[] = [];
  for (let i = 0; i < n; i++) {
    const role = i % 2 === 0 ? 'user' : 'orchestrator';
    // Deliberately short one-liners: their real rendered height is well
    // below the 120px virtual estimate, which is what triggered the bug.
    const text = i === n - 1 ? NEWEST_MARKER : `Short turn ${i}`;
    turns.push({
      id: `turn-${String(i).padStart(3, '0')}`,
      ts: new Date(base + i * 60_000).toISOString(),
      role,
      text,
    });
  }
  return turns;
}

const TURNS = buildTurns(200);

async function stubOrchestratorChat(page: Page): Promise<void> {
  // Exact-path match for the chat GET; the `/attachments/...` sub-route
  // lives deeper so this glob never swallows it.
  await page.route('**/api/runner/*/orchestrator-chat', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.fallback();
      return;
    }
    const project =
      decodeURIComponent(route.request().url().match(/runner\/([^/]+)\/orchestrator-chat/)?.[1] ?? 'demo');
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project, turns: TURNS }),
    });
  });
}

async function openOrchestratorChat(page: Page) {
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  await toggle.click();

  const sheet = page.getByTestId('orch-side-sheet');
  await expect(sheet).toBeVisible();
  await expect
    .poll(() => sheet.evaluate((el) => (el as HTMLElement).offsetWidth), {
      message: 'orchestrator side sheet width',
    })
    .toBeGreaterThan(300);

  const body = page.getByTestId('chat-body');
  if (!(await body.count())) {
    test.skip(true, 'No watched project auto-selected — orchestrator chat body did not mount');
  }
  await expect(body).toBeVisible();
  // Wait for the stubbed turns to render. We do NOT wait on the newest
  // marker here: on the broken build it can already be windowed out, so
  // the precondition must be a turn that renders in both builds.
  await expect(page.getByTestId('chat-msg-user').first()).toBeVisible();
  return { sheet, body };
}

test.describe('orchestrator chat — content stays visible after load', () => {
  test('newest turn survives a scroll fired while sticky at the bottom', async ({ page }) => {
    await stubOrchestratorChat(page);
    const { body } = await openOrchestratorChat(page);

    // Reproduce the trigger deterministically AND read the result in one
    // shot: park the scroller at the true bottom (sticky), fire a scroll
    // event exactly like the open-animation / markdown-growth / programmatic-
    // pin reflow does, wait two animation frames for the window recompute,
    // then capture marker + spacer state synchronously — all inside a single
    // evaluate. This MUST be a single-shot read: the side sheet polls the
    // chat in the background and re-pins the tail, which heals the transient
    // vanish within a second. A retrying matcher (toBeVisible) would let it
    // heal and pass on the broken build, masking the regression. We snapshot
    // the DOM the instant after the scroll, before any poll can run.
    const snapshot = await body.evaluate(async (el, marker) => {
      const nextFrame = () =>
        new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      el.scrollTop = el.scrollHeight;
      el.dispatchEvent(new Event('scroll'));
      await nextFrame();
      await nextFrame();

      const rows = Array.from(
        el.querySelectorAll('[data-testid^="chat-msg-"]')
      ) as HTMLElement[];
      const markerEl = rows.find((r) => (r.textContent ?? '').includes(marker));
      const containerRect = el.getBoundingClientRect();
      const rect = markerEl?.getBoundingClientRect();
      const spacer = el.querySelector(
        '[data-testid="chat-spacer-bottom"]'
      ) as HTMLElement | null;

      return {
        markerRendered: !!markerEl,
        // Vertically overlapping the scroll container = on screen, no manual
        // scroll needed.
        markerInViewport:
          !!rect &&
          rect.bottom > containerRect.top &&
          rect.top < containerRect.bottom,
        bottomSpacerHeight: spacer ? spacer.getBoundingClientRect().height : 0,
      };
    }, NEWEST_MARKER);

    // The newest turn must be rendered AND on screen — no manual scroll.
    expect(snapshot.markerRendered).toBe(true);
    expect(snapshot.markerInViewport).toBe(true);
    // And there must be no phantom bottom spacer hiding the tail.
    expect(snapshot.bottomSpacerHeight).toBe(0);

    await page.screenshot({ path: `${SHOTS}/01-newest-turn-visible-after-load.png` });
  });
});
