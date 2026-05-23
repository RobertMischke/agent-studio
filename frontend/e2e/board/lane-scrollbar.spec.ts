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
 * The spec runs against whatever board state the configured backend
 * exposes — no fixtures. The CSS contract is what F28 cares about; any
 * lane on the board is enough to validate it.
 *
 * Asserts:
 *   1. `.column__body` has `scrollbar-gutter: stable`.
 *   2. The 4 px right-padding hack is gone (padding-right is 0).
 *   3. Two side-by-side lanes share the same content width — the
 *      gutter reservation does not depend on the lane's overflow state,
 *      so empty/full lanes lay out identically.
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
  test('column__body uses scrollbar-gutter: stable and drops the old 4px padding-right hack', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    // Wait until at least one lane body has rendered. We don't care
    // which lane — F28 is about every lane's scroll surface, so the
    // first one we find is representative.
    const body = page.locator('.column__body').first();
    await expect(body, 'expected at least one .column__body on the board').toBeVisible({ timeout: 10_000 });

    const styles = await body.evaluate((el) => {
      const cs = window.getComputedStyle(el);
      return {
        scrollbarGutter: cs.scrollbarGutter,
        paddingRight: cs.paddingRight,
        overflowY: cs.overflowY,
      };
    });

    // 1. The new gutter mechanism is in effect.
    expect(
      styles.scrollbarGutter,
      `column__body scrollbar-gutter="${styles.scrollbarGutter}" — F28 expects "stable" so the lane ` +
      `reserves gutter width at layout time instead of forcing a manual padding-right.`,
    ).toMatch(/\bstable\b/);

    // 2. The old 4 px right padding hack is gone — its job is now done
    //    by scrollbar-gutter. Painting it again would put us back in
    //    the redundant-track state F28 fixes.
    expect(
      styles.paddingRight,
      `column__body padding-right="${styles.paddingRight}" — F28 removed the manual 4 px gutter; ` +
      `scrollbar-gutter handles the reservation now.`,
    ).toBe('0px');

    // 3. overflow-y stays auto so the lane still scrolls when full.
    expect(styles.overflowY).toMatch(/^(auto|scroll)$/);
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
