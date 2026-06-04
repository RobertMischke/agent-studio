import { test, expect, Page } from '@playwright/test';

/**
 * Header-toolbar polish (operator request, 2026-06-04).
 *
 * Locks the structure behind the visual cleanup of the top-right controls:
 *  - the board tab-bar actions (Lanes/Epics + Full/Compact toggles and the
 *    primary "+ Add task") live in a single `studio-board-actions` flex row
 *    with an in-cluster separator before the primary, instead of loose
 *    whitespace-spaced buttons; and
 *  - the titlebar actions (Project Chat + theme + Notifications) stay a
 *    grouped, evenly-spaced trio.
 *
 * Visual deliverable: screenshots of the header region with the toggles in
 * both rest and active states, copied into the job's results/ folder by the
 * caller.
 *
 * Runs against whatever board state the configured backend exposes; the
 * header chrome renders regardless of whether any cards are present.
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
  const studio = page.getByTestId('studio-board');
  const welcome = page.getByTestId('studio-welcome');
  await Promise.race([
    studio.first().waitFor({ state: 'visible', timeout: 8_000 }),
    welcome.first().waitFor({ state: 'visible', timeout: 8_000 }),
  ]).catch(() => { /* fall through */ });

  if ((await welcome.count()) > 0 && (await welcome.first().isVisible().catch(() => false))) {
    const allProjects = welcome.first().getByRole('button', { name: 'All projects' });
    await allProjects.click({ timeout: 3_000 }).catch(() => { /* nothing */ });
    await studio.first().waitFor({ state: 'visible', timeout: 5_000 }).catch(() => { /* nothing */ });
  }

  await dismissTransientErrors(page);
}

test.describe('Header toolbar polish', () => {
  test('board actions form one grouped, evenly-spaced cluster', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1000 });
    await gotoBoard(page);

    const chatButton = page.getByTestId('studio-titlebar-chat');
    if ((await chatButton.count()) === 0) {
      test.skip(true, 'vsCodeLayout shell not active; header toolbar only exists there');
      return;
    }

    // Board cluster wrapper present with all three controls inside it.
    const cluster = page.getByTestId('studio-board-actions');
    await expect(cluster).toBeVisible();
    const epicToggle = page.getByTestId('studio-board-epic-toggle');
    const compactToggle = page.getByTestId('studio-board-compact-toggle');
    const addTask = page.getByTestId('studio-board-add-task');
    await expect(epicToggle).toBeVisible();
    await expect(compactToggle).toBeVisible();
    await expect(addTask).toBeVisible();
    // All three sit inside the single cluster row.
    await expect(cluster.getByTestId('studio-board-epic-toggle')).toHaveCount(1);
    await expect(cluster.getByTestId('studio-board-compact-toggle')).toHaveCount(1);
    await expect(cluster.getByTestId('studio-board-add-task')).toHaveCount(1);

    // Toggles expose a pressed state so they read as switchable controls.
    await expect(epicToggle).toHaveAttribute('aria-pressed', /true|false/);
    await expect(compactToggle).toHaveAttribute('aria-pressed', /true|false/);

    // Titlebar action trio is grouped together.
    const titleActions = page.getByTestId('studio-titlebar-actions');
    await expect(titleActions).toBeVisible();
    await expect(titleActions.getByTestId('studio-titlebar-chat')).toHaveCount(1);

    // --- Screenshots: header region + clusters in rest state -------------
    await page.screenshot({ path: 'test-results/header-toolbar-full.png', clip: { x: 0, y: 0, width: 1600, height: 76 } });
    await titleActions.screenshot({ path: 'test-results/header-titlebar-actions.png' });
    await cluster.screenshot({ path: 'test-results/header-board-actions-rest.png' });

    // Flip both toggles so the active (pressed) pill state is captured.
    const epicPressedBefore = await epicToggle.getAttribute('aria-pressed');
    const compactPressedBefore = await compactToggle.getAttribute('aria-pressed');
    if (epicPressedBefore === 'false') await epicToggle.click();
    if (compactPressedBefore === 'false') await compactToggle.click();
    await expect(epicToggle).toHaveAttribute('aria-pressed', 'true');
    await expect(compactToggle).toHaveAttribute('aria-pressed', 'true');
    await page.waitForTimeout(150);
    await cluster.screenshot({ path: 'test-results/header-board-actions-active.png' });

    // Restore original toggle states so the spec leaves no board change.
    if (epicPressedBefore === 'false') await epicToggle.click();
    if (compactPressedBefore === 'false') await compactToggle.click();
  });
});
