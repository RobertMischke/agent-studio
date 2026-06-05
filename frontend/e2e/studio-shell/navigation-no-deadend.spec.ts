import { test, expect, type Page } from '@playwright/test';

/**
 * Bug `human-decision-needed-bug-navigation-deadend-when-no-task-open`:
 * when every editor tab is closed, the studio shell used to render an
 * empty "Welcome" limbo with no clear way back to the board. The fix
 * keeps the editor surface anchored on the always-mounted sticky default
 * board tab via two independent recovery paths:
 *
 *   1. The tab list always contains a sticky `board:__all__` tab that
 *      cannot be closed (no X button, close-* service ops preserve it).
 *      Closing every other tab leaves the sticky tab active.
 *   2. Ctrl+B (Cmd+B on macOS) is a global board<->backlog-triage toggle:
 *      from a plain tab it opens the triage screen, and pressing it again
 *      closes triage and re-activates the sticky board. So Ctrl+B always
 *      leads back to the board, regardless of the starting view (outside a
 *      text input). NOTE: Ctrl+B was repurposed from a one-shot "focus the
 *      board" shortcut into this toggle by the `feat(backlog): add dedicated
 *      triage screen` change; the test below tracks that current contract.
 *
 * The activity bar no longer carries a dedicated Board button (removed
 * so the cross-project "All projects" board opens only via the grid
 * button in the Explorer panel header). That entry point is covered by
 * `activity-bar-board-removed.spec.ts`.
 *
 * This spec exercises both remaining paths so a regression in either one
 * fails loudly. Each assertion maps directly to an acceptance bullet in
 * the bug ticket.
 */

const STICKY_TAB_KEY = 'board:__all__';

/** Empty-but-valid GroupedJobs payload (mirrors the service signal default). */
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function bootStudio(page: Page): Promise<void> {
  // This spec exercises a purely front-end concern (sticky tab + recovery
  // paths), so it must not depend on a live backend. With the backend down
  // every boot GET 500s and the shell raises a blocking <app-error-dialog>
  // overlay that intercepts pointer events (Board button, tab close X).
  // Stub the boot endpoints with empty-but-valid payloads so no overlay ever
  // appears. The stub persists for the page lifetime, covering the initial
  // load, every reload, and background polls. Against a live backend it is a
  // harmless no-data response that does not affect the navigation assertions.
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    // Only these four boot GETs surface a *blocking* error-dialog overlay
    // when they fail; stub them with empty-but-valid payloads.
    if (url.includes('/api/tasks/grouped')) return json(EMPTY_GROUPED);
    if (url.includes('/api/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/watch-paths')) return json([]);
    // Everything else (quota, workspaces, config, …) is left to fail the way
    // the app already handles it — inline, non-blocking. Returning a wrong-
    // shaped success here would crash a consumer (e.g. the header quota cards).
    return route.continue();
  });

  // Each Playwright test runs in its own fresh browser context, so
  // localStorage already starts empty and the tab-state service mounts the
  // sticky board tab on construction. We deliberately do NOT register a
  // persistent addInitScript that clears the storage key: such a script
  // re-runs on every navigation, and the seeded tests below set the key and
  // then reload — a persistent clear would wipe the seed before the app boots.
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
          { kind: 'task', taskKey: 'fake-jobkey-a' },
          { kind: 'task', taskKey: 'fake-jobkey-b' },
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

  test('Epics is a normal closeable editor tab', async ({ page }) => {
    await bootStudio(page);
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__', sticky: true },
          { kind: 'epics', projectName: null },
        ],
        activeKey: 'epics:__all__',
      };
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    });
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });

    const epicsTab = tabBy(page, 'epics:__all__');
    await expect(epicsTab).toBeVisible();
    await expect(epicsTab).toHaveClass(/studio-tab--active/);
    await expect(epicsTab).not.toHaveAttribute('data-sticky', 'true');
    await expect(epicsTab.locator('[data-testid="studio-tab-pin"]')).toHaveCount(0);
    await expect(epicsTab.locator('.studio-tab__close')).toBeVisible();
    await expect(page.getByTestId('epic-overview-close')).toHaveCount(0);

    await epicsTab.locator('.studio-tab__close').click();
    await expect(epicsTab).toHaveCount(0);
    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveClass(/studio-tab--active/);
  });

  test('Ctrl+B toggles through the backlog triage and back to the sticky board tab', async ({ page }) => {
    await bootStudio(page);
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__', sticky: true },
          { kind: 'task', taskKey: 'fake-jobkey-shortcut' },
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
    //
    // Ctrl+B is a toggle. From a plain task tab the first press opens the
    // backlog triage screen (it does NOT jump straight to the board).
    await page.locator('body').focus();
    await page.keyboard.press('Control+B');
    await expect(page.getByTestId('backlog-triage-screen')).toBeVisible();
    await expect(tabBy(page, STICKY_TAB_KEY)).not.toHaveClass(/studio-tab--active/);

    // The second press closes triage and re-activates the sticky board, so
    // Ctrl+B still always leads back to the board — no dead end.
    await page.locator('body').focus();
    await page.keyboard.press('Control+B');
    await expect(page.getByTestId('backlog-triage-screen')).toHaveCount(0);
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
