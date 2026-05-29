import { test, expect, type Page } from '@playwright/test';

/**
 * Bug `human-decision-needed-bug-navigation-deadend-when-no-task-open`:
 * when every editor tab is closed, the studio shell used to render an
 * empty "Welcome" limbo with no clear way back to the board. The fix
 * adds three independent recovery paths that all converge on the
 * always-mounted sticky default board tab:
 *
 *   1. The tab list always contains a sticky `board:__all__` tab that
 *      cannot be closed (no X button, close-* service ops preserve it).
 *      Closing every other tab leaves the sticky tab active.
 *   2. The activity bar carries a dedicated Board button at the top of
 *      the rail that re-activates the sticky tab from any other state.
 *   3. Ctrl+B (Cmd+B on macOS) globally focuses the sticky tab from
 *      any view that is not a text input.
 *
 * This spec exercises all three so a regression in any one of them
 * fails loudly. Each assertion maps directly to an acceptance bullet
 * in the bug ticket.
 */

const STICKY_TAB_KEY = 'board:__all__';

async function bootStudio(page: Page): Promise<void> {
  // Reset persisted tab state so each test starts from a known-empty
  // baseline (the service then re-mounts the sticky tab on construction).
  await page.addInitScript(() => {
    try { localStorage.removeItem('atp.studio.tabs.v1'); } catch { /* ignore */ }
  });
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
}

function tabBy(page: Page, key: string) {
  return page.locator(`.studio-tab[data-tab-key="${key}"]`);
}

test.describe('studio-shell · navigation has no dead end', () => {
  test.setTimeout(45_000);

  test('sticky board tab is mounted at boot and cannot be closed', async ({ page }) => {
    await bootStudio(page);

    const sticky = tabBy(page, STICKY_TAB_KEY);
    await expect(sticky).toBeVisible();
    // Sticky tabs render the pin glyph in place of the close X.
    await expect(sticky.locator('[data-testid="studio-tab-pin"]')).toBeVisible();
    await expect(sticky.locator('.studio-tab__close')).toHaveCount(0);
    // The sticky tab also carries the data attribute so other specs can
    // reuse it for assertions without coupling to the icon DOM.
    await expect(sticky).toHaveAttribute('data-sticky', 'true');
  });

  test('opening 2 other tabs then closing every closeable tab leaves the sticky board active', async ({ page }) => {
    await bootStudio(page);

    // Seed two extra non-sticky tabs via the persistence boundary so the
    // assertion doesn't depend on having real jobs in the fixture
    // backend. The service restores them on next render.
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__', sticky: true },
          { kind: 'task', jobKey: 'fake-jobkey-a' },
          { kind: 'task', jobKey: 'fake-jobkey-b' },
        ],
        activeKey: 'task:fake-jobkey-b',
      };
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    });
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });

    // Three tabs visible, one of them sticky.
    await expect(page.locator('.studio-tab')).toHaveCount(3);
    const stickyTab = tabBy(page, STICKY_TAB_KEY);
    const taskA = tabBy(page, 'task:fake-jobkey-a');
    const taskB = tabBy(page, 'task:fake-jobkey-b');

    // Close both task tabs via the X button.
    await taskB.locator('.studio-tab__close').click();
    await expect(taskB).toHaveCount(0);
    await taskA.locator('.studio-tab__close').click();
    await expect(taskA).toHaveCount(0);

    // Sticky tab is now the only one, and it is active.
    await expect(page.locator('.studio-tab')).toHaveCount(1);
    await expect(stickyTab).toBeVisible();
    await expect(stickyTab).toHaveClass(/studio-tab--active/);
    // The empty-state welcome card MUST NOT render — that is the limbo
    // state the bug was filed against.
    await expect(page.getByTestId('studio-welcome')).toHaveCount(0);
  });

  test('activity-bar Board button is always visible and activates the sticky tab', async ({ page }) => {
    await bootStudio(page);

    const boardBtn = page.getByTestId('studio-ab-board');
    await expect(boardBtn).toBeVisible();
    await expect(boardBtn).toHaveAttribute('title', /Open board/);

    // Move active focus off the sticky tab via persistence so we can
    // assert the button SWITCHES context (not just no-op).
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__', sticky: true },
          { kind: 'task', jobKey: 'fake-jobkey-x' },
        ],
        activeKey: 'task:fake-jobkey-x',
      };
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    });
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
    await expect(tabBy(page, 'task:fake-jobkey-x')).toHaveClass(/studio-tab--active/);

    // Click the Board button — sticky tab takes focus.
    await page.getByTestId('studio-ab-board').click();
    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveClass(/studio-tab--active/);
    await expect(tabBy(page, 'task:fake-jobkey-x')).not.toHaveClass(/studio-tab--active/);
  });

  test('Ctrl+B from any tab focuses the sticky board tab', async ({ page }) => {
    await bootStudio(page);
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__', sticky: true },
          { kind: 'task', jobKey: 'fake-jobkey-shortcut' },
        ],
        activeKey: 'task:fake-jobkey-shortcut',
      };
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    });
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
    await expect(tabBy(page, 'task:fake-jobkey-shortcut')).toHaveClass(/studio-tab--active/);

    // Fire Ctrl+B against the document body so the listener on `window`
    // catches it. Playwright's keyboard.press dispatches to the focused
    // element by default.
    await page.locator('body').focus();
    await page.keyboard.press('Control+B');
    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveClass(/studio-tab--active/);
  });

  test('sticky board tab persists across a reload', async ({ page }) => {
    await bootStudio(page);
    await expect(tabBy(page, STICKY_TAB_KEY)).toBeVisible();
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
    await expect(tabBy(page, STICKY_TAB_KEY)).toBeVisible();
    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveAttribute('data-sticky', 'true');
  });
});
