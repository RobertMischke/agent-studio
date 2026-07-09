import { test, expect, Page } from '@playwright/test';

/**
 * MC-2 (Concept §4): the orchestrator side sheet follows the operator's
 * navigation and can be pinned to freeze the current context.
 *
 * The chat + task endpoints are stubbed so the spec runs without a live
 * backend or CLI quota. What we lock here (real-app wiring; the exhaustive
 * task-vs-board derivation and freeze/resume is in the component unit spec):
 *   1. On the board the header derives a `project:<name>` context key and the
 *      toolbar shows an unpinned "Pin" button.
 *   2. Clicking Pin flips the header into the pinned state (`data-pinned`,
 *      the "Pinned" chip) and disables the project picker.
 *   3. Clicking again unpins and re-enables the picker.
 */

const PROJECT = 'project-neuen';

async function stubWorkspace(page: Page) {
  // Hermetic catch-all so the spec runs without a live backend (its stated
  // design). Registered first, so the specific stubs below take precedence
  // (Playwright matches routes in reverse registration order). Without this,
  // the board's background polls (cli models/quota, runner status, environment,
  // projects/settings, crash-recovery, archive, …) hit the down backend, return
  // status 0, and pop the shared "Backend not reachable" error dialog whose
  // overlay intercepts every click. Most endpoints tolerate an empty object;
  // the ones below whose consumers read a nested array (`.length`/`Object.keys`)
  // or that are list-shaped need a minimally-valid empty body so nothing throws.
  const emptyBodyFor = (path: string): string => {
    if (/\/api\/(tags|workspaces|clients)\/?$/.test(path)) return '[]';
    if (/\/api\/runner\/status$/.test(path)) return '{"projects":{}}';
    if (/\/api\/cli\/quota$/.test(path)) return '{"snapshots":[]}';
    if (/\/api\/tasks\/archive/.test(path)) return '{"items":[],"total":0,"offset":0,"limit":0}';
    return '{}';
  };
  await page.route(/\/api\//, async (route) => {
    const path = new URL(route.request().url()).pathname;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: emptyBodyFor(path),
    });
  });

  await page.route(/\/api\/watch-paths$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: 'C:/tmp/' + PROJECT, rootPath: 'C:/tmp/' + PROJECT, repositoryPath: '' },
      ]),
    });
  });

  await page.route(/\/api\/tasks(?:\?.*)?$/, async (route) => {
    if (route.request().method() !== 'GET') { await route.continue(); return; }
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });

  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [],
      }),
    });
  });

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(route.request().url());
    const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project, turns: [] }),
    });
  });
}

/**
 * The dev backend is offline in this run, so background polls for endpoints
 * we do not stub fail and pop the shared error dialog. Its overlay would
 * intercept our clicks; dismiss any that are open before proceeding.
 */
async function dismissErrorDialogs(page: Page) {
  for (let i = 0; i < 5; i++) {
    const overlay = page.getByTestId('error-dialog-overlay');
    if ((await overlay.count()) === 0) return;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
  }
}

async function openSideSheet(page: Page) {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await dismissErrorDialogs(page);
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
}

test.describe('Orchestrator side sheet · navigation context + pin', () => {
  test('board scope derives a project context key and pin toggles the frozen state', async ({ page }) => {
    await stubWorkspace(page);
    await openSideSheet(page);

    const header = page.getByTestId('orch-context-header');
    await expect(header).toBeVisible();
    await expect(header).toHaveAttribute('data-scope', 'board');
    await expect(header).toHaveAttribute('data-context-key', `project:${PROJECT}`);
    // Not pinned yet.
    await expect(header).not.toHaveAttribute('data-pinned', 'true');
    await expect(page.getByTestId('orch-context-pin')).toHaveCount(0);

    const pinBtn = page.getByTestId('orch-side-sheet-pin');
    await expect(pinBtn).toContainText('Pin');
    await expect(pinBtn).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeEnabled();

    await page.screenshot({
      path: 'screenshots/orchestrator-side-sheet-pin/following--mocked.png',
      fullPage: false,
    });

    // Pin: header enters the pinned state, the picker locks.
    await pinBtn.click();
    await expect(header).toHaveAttribute('data-pinned', 'true');
    await expect(page.getByTestId('orch-context-pin')).toContainText('Pinned');
    await expect(pinBtn).toHaveAttribute('aria-pressed', 'true');
    await expect(pinBtn).toContainText('Pinned');
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeDisabled();

    await page.screenshot({
      path: 'screenshots/orchestrator-side-sheet-pin/pinned--mocked.png',
      fullPage: false,
    });

    // Unpin: back to following navigation.
    await pinBtn.click();
    await expect(header).not.toHaveAttribute('data-pinned', 'true');
    await expect(page.getByTestId('orch-context-pin')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeEnabled();
  });
});
