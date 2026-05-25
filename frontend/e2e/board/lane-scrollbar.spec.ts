import { test, expect, Page } from '@playwright/test';

/**
 * F28 — Lane scrollbars must stay quiet.
 *
 * Symptom (operator screenshot 2026-05-22): every lane painted a thin
 * vertical scrollbar track on its right edge even when the lane held
 * only 2-3 cards. The cause was a manual `padding-right: 4 px` gutter
 * on `.column__body` — Chromium painted an empty track inside that
 * reserved strip whenever the lane had `overflow-y: auto` set, which
 * read as a permanent visual border between lanes.
 *
 * Fix: replace the manual padding with `scrollbar-gutter: stable` plus
 * a `thin-scroll` mixin that keeps the thumb transparent until the
 * cursor enters the body. The gutter is still reserved at the layout
 * level so a lane's width does not jump when it crosses from "no
 * overflow" to "overflowing", but no track is painted while the lane
 * fits.
 *
 * F60 refinement: in the studio super-column layout, the scroll surface
 * moved from `.column__body` to `.lane-group__lanes` — one scrollbar
 * per super-column, not per lane. The legacy layout keeps the per-lane
 * `.column__body` scroll. This test probes whichever surface is active.
 *
 * The spec runs against whatever board state the configured backend
 * exposes — no fixtures. The CSS contract is what F28 cares about; any
 * lane on the board is enough to validate it.
 *
 * Asserts:
 *   1. The active scroll surface has `scrollbar-gutter: stable`.
 *   2. The 4 px right-padding hack is gone on `.column__body`.
 *   3. Two lanes share the same content width — the gutter reservation
 *      does not depend on the lane's overflow state.
 */

async function dismissTransientErrors(page: Page): Promise<void> {
  for (let i = 0; i < 3; i++) {
    const overlay = page.locator('.overlay--error');
    if ((await overlay.count()) === 0) break;
    if (!(await overlay.first().isVisible({ timeout: 200 }).catch(() => false))) break;
    await page.locator('.error-dialog__close').first().click({ timeout: 1_000 }).catch(() => {});
    await page.waitForTimeout(150);
  }
}

async function gotoBoard(page: Page): Promise<void> {
  await page.goto('/');
  // Studio shell lands on Welcome when no tab is open; clicking
  // "All projects" opens the board tab. Legacy shell exposed
  // `kanban-dashboard` at root. Accept either.
  const studio = page.getByTestId('studio-board');
  const legacy = page.getByTestId('kanban-dashboard');
  const welcome = page.getByTestId('studio-welcome');
  await Promise.race([
    studio.first().waitFor({ state: 'visible', timeout: 8_000 }),
    legacy.first().waitFor({ state: 'visible', timeout: 8_000 }),
    welcome.first().waitFor({ state: 'visible', timeout: 8_000 }),
  ]).catch(() => { /* fall through to the explicit board open below */ });

  if ((await welcome.count()) > 0 && (await welcome.first().isVisible().catch(() => false))) {
    const allProjects = welcome.first().getByRole('button', { name: 'All projects' });
    await allProjects.click({ timeout: 3_000 }).catch(() => { /* tolerate the legacy shell */ });
    await studio.first().waitFor({ state: 'visible', timeout: 5_000 }).catch(() => { /* nothing */ });
  }

  expect(
    (await studio.count()) + (await legacy.count()),
    'either studio-board or kanban-dashboard should be visible',
  ).toBeGreaterThan(0);
  await dismissTransientErrors(page);
}

test.describe('F28 — lane scrollbar redundancy', () => {
  test('scroll surface uses scrollbar-gutter: stable and the old 4px padding-right hack is gone', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const body = page.locator('.column__body').first();
    await expect(body, 'expected at least one .column__body on the board').toBeVisible({ timeout: 10_000 });

    // Detect layout: studio super-column vs legacy horizontal.
    // In studio, the scroll surface is .lane-group__lanes; in legacy
    // it is .column__body.
    const isStudio = (await page.getByTestId('studio-board').count()) > 0;

    if (isStudio) {
      const lanes = page.locator('.lane-group__lanes').first();
      await expect(lanes).toBeVisible({ timeout: 5_000 });
      const styles = await lanes.evaluate((el) => {
        const cs = window.getComputedStyle(el);
        return { scrollbarGutter: cs.scrollbarGutter, overflowY: cs.overflowY };
      });

      expect(
        styles.scrollbarGutter,
        `lane-group__lanes scrollbar-gutter="${styles.scrollbarGutter}" — F60 moved the gutter ` +
        `reservation to the super-column scroll surface.`,
      ).toMatch(/\bstable\b/);
      expect(styles.overflowY).toMatch(/^(auto|scroll)$/);

      // column__body must NOT scroll in the studio layout.
      const bodyStyles = await body.evaluate((el) => {
        const cs = window.getComputedStyle(el);
        return { overflowY: cs.overflowY };
      });
      expect(
        bodyStyles.overflowY,
        `column__body overflow-y="${bodyStyles.overflowY}" — F60 delegates scrolling to ` +
        `lane-group__lanes; the per-lane body must not scroll.`,
      ).toBe('visible');
    } else {
      const styles = await body.evaluate((el) => {
        const cs = window.getComputedStyle(el);
        return { scrollbarGutter: cs.scrollbarGutter, overflowY: cs.overflowY };
      });
      expect(styles.scrollbarGutter).toMatch(/\bstable\b/);
      expect(styles.overflowY).toMatch(/^(auto|scroll)$/);
    }

    // The 4 px right padding hack must be gone in both layouts.
    const paddingRight = await body.evaluate((el) =>
      window.getComputedStyle(el).paddingRight,
    );
    expect(
      paddingRight,
      `column__body padding-right="${paddingRight}" — the manual 4 px gutter must stay removed.`,
    ).toBe('0px');
  });

  test('two lanes share the same content width regardless of their overflow state', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const bodies = page.locator('.column__body');
    await expect(bodies.first()).toBeVisible({ timeout: 10_000 });
    const count = await bodies.count();
    if (count < 2) {
      test.skip(true, `need at least 2 lanes on the board to compare widths; got ${count}`);
      return;
    }

    // Find one overflowing lane and one non-overflowing lane so we
    // exercise the "scrollbar-gutter must not shift the layout
    // depending on overflow state" contract. Fall back to comparing
    // the first two lanes if no overflow exists today on the board.
    const probe = await page.evaluate(() => {
      const all = Array.from(document.querySelectorAll('.column__body')) as HTMLElement[];
      const widths = all.map((el, i) => ({
        idx: i,
        clientWidth: el.clientWidth,
        overflows: el.scrollHeight > el.clientHeight + 1,
      }));
      const overflowing = widths.find((w) => w.overflows);
      const empty = widths.find((w) => !w.overflows);
      return {
        widths,
        overflowingIdx: overflowing?.idx ?? -1,
        emptyIdx: empty?.idx ?? -1,
      };
    });

    // Pick the pair: overflowing + empty if available, else widths[0]
    // and widths[1].
    let a: { idx: number; clientWidth: number };
    let b: { idx: number; clientWidth: number };
    if (probe.overflowingIdx >= 0 && probe.emptyIdx >= 0 && probe.overflowingIdx !== probe.emptyIdx) {
      a = probe.widths[probe.overflowingIdx];
      b = probe.widths[probe.emptyIdx];
    } else {
      a = probe.widths[0];
      b = probe.widths[1];
    }

    // Lanes share a flex track so their widths should be byte-identical;
    // allow a 2 px slack for sub-pixel rounding only.
    const delta = Math.abs(a.clientWidth - b.clientWidth);
    expect(
      delta,
      `Lane content widths differ by ${delta}px (lane ${a.idx}=${a.clientWidth}, lane ${b.idx}=` +
      `${b.clientWidth}). With scrollbar-gutter: stable both lanes must reserve the same gutter width ` +
      `regardless of overflow state — otherwise cards in the empty lane would expand into the gutter ` +
      `and reflow on the first overflow.`,
    ).toBeLessThanOrEqual(2);
  });
});
