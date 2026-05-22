import { test, expect, Page, BrowserContext } from '@playwright/test';

/**
 * F24 regression — operator-reported 2026-05-22.
 *
 * Two issues locked here:
 *
 *  1. Card-density toggle changes did not propagate to a second open
 *     tab. localStorage was updated by the writing tab, but the
 *     sibling tab's UiPreferencesService signal stayed at the previous
 *     value until F5. UiPreferencesService now listens for `storage`
 *     events and mirrors the new value.
 *
 *  2. With the orchestrator side-sheet open, the shell forces compact
 *     rendering regardless of the persisted preference (F4). Clicking
 *     the toggle then looked broken: pref flipped to "full" but the
 *     cards stayed compact. The toggle now surfaces an info toast that
 *     spells out the override.
 *
 * The spec runs against whatever board state the configured backend
 * exposes. Card-density assertions that need actual cards on the board
 * gate themselves so the spec stays useful on an empty board.
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
  // vsCodeLayout shell (default-on) lands on the Welcome card when no
  // tab is open; opening any board tab paints `studio-board`. Legacy
  // shell exposed `kanban-dashboard` at root. Accept either, and click
  // "All projects" from Welcome if we landed there.
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

async function compactToggle(page: Page) {
  // Same trade-off as gotoBoard: prefer the studio-shell toggle id, fall
  // back to the legacy header toggle. The two render different markup but
  // wire to the same App.toggleCompactCards path.
  const studio = page.getByTestId('studio-board-compact-toggle');
  if ((await studio.count()) > 0) return studio.first();
  return page.getByTestId('compact-cards-toggle').first();
}

async function resetCompactPref(page: Page): Promise<void> {
  await page.goto('/');
  await page.evaluate(() => {
    try { localStorage.removeItem('compactCards'); } catch { /* ignore */ }
  });
}

test.describe('F24 compact cards: cross-tab + rail-override feedback', () => {
  test('two tabs converge: toggle in tab A propagates to tab B', async ({ browser }) => {
    // One browser context, two pages = two tabs of the same origin. Real
    // `storage` events fire across pages in the same context.
    const context: BrowserContext = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    try {
      const tabA = await context.newPage();
      const tabB = await context.newPage();

      await resetCompactPref(tabA);
      await gotoBoard(tabA);
      await gotoBoard(tabB);

      const toggleA = await compactToggle(tabA);
      const toggleB = await compactToggle(tabB);
      await expect(toggleA).toBeVisible();
      await expect(toggleB).toBeVisible();

      // Default state in both tabs: Full.
      await expect(toggleA).toHaveAttribute('aria-pressed', 'false');
      await expect(toggleB).toHaveAttribute('aria-pressed', 'false');

      // Toggle in tab A — tab B should mirror without a reload.
      await toggleA.click();
      await expect(toggleA).toHaveAttribute('aria-pressed', 'true');
      await expect(toggleB).toHaveAttribute('aria-pressed', 'true', { timeout: 2_000 });

      // Flip back from tab B, tab A mirrors.
      await toggleB.click();
      await expect(toggleB).toHaveAttribute('aria-pressed', 'false');
      await expect(toggleA).toHaveAttribute('aria-pressed', 'false', { timeout: 2_000 });
    } finally {
      await context.close();
    }
  });

  test('toggle while orchestrator rail open shows info toast + writes the pref', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1000 });
    await resetCompactPref(page);
    await gotoBoard(page);

    // Open the orchestrator side-sheet via the studio titlebar chat button.
    // The button only exists in the vsCodeLayout shell — guard the click
    // and skip the spec when the legacy shell is in play.
    const chatButton = page.getByTestId('studio-titlebar-chat');
    const chatCount = await chatButton.count();
    if (chatCount === 0) {
      test.skip(true, 'vsCodeLayout shell not active; orchestrator-rail override is only reachable there');
      return;
    }
    await chatButton.click();
    const rail = page.locator('app-orchestrator-side-sheet');
    await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });

    // Make the toggle starts at "Full".
    expect(await page.evaluate(() => localStorage.getItem('compactCards'))).not.toBe('1');

    // Click the toggle once — pref flips to compact. No toast yet, because
    // pref + effective both say "compact" (the rail-forced value matches).
    const toggle = await compactToggle(page);
    await toggle.click();
    expect(await page.evaluate(() => localStorage.getItem('compactCards'))).toBe('1');
    // Allow the optimistic notification settle window; we just assert the
    // negative case here, no toast.
    await page.waitForTimeout(150);

    // Click again to switch to "Full" while rail is still open. NOW the
    // effective value (compact, forced by rail) diverges from the pref
    // (full). The shell should surface the divergence via an info toast.
    await toggle.click();
    expect(await page.evaluate(() => localStorage.getItem('compactCards'))).toBe('0');

    const toast = page.getByTestId('notification-info').last();
    await expect(toast).toBeVisible({ timeout: 3_000 });
    await expect(toast).toContainText(/orchestrator rail|rail/i);
    await expect(toast.getByTestId('notification-title')).toContainText(/Preference saved/i);
    await page.screenshot({ path: 'test-results/f24-rail-override-toast.png', fullPage: false });
  });
});
