import { test, expect, Page } from '@playwright/test';
import * as path from 'node:path';

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

  await page.route(/\/api\/orchestrator\/sessions$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ sessions: [
        {
          contextKey: `project:${PROJECT}`, kind: 'project', projectId: PROJECT, taskKey: null,
          updatedAt: '2026-07-11T10:00:00Z', model: 'codex', cumulativeInputTokens: 800,
          cumulativeOutputTokens: 200, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
          runtimeStatus: 'active', queuePosition: 0,
        },
        {
          contextKey: `task:${PROJECT}/AGT-1933`, kind: 'task', projectId: PROJECT, taskKey: 'AGT-1933',
          updatedAt: '2026-07-11T10:01:00Z', model: 'codex', cumulativeInputTokens: 12_000,
          cumulativeOutputTokens: 3_000, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
          runtimeStatus: 'parked', queuePosition: 0,
        },
      ] }),
    });
  });

  await page.route(new RegExp(`/api/orchestrator/context/project:${PROJECT}(?:/refresh)?$`), async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        contextKey: `project:${PROJECT}`,
        capturedAt: new Date().toISOString(),
        digest: 'lanes: ready=0 | runs: active=0 | health: ok',
        sources: [
          { name: 'lanes', status: 'empty', capturedAt: new Date().toISOString(), detail: null },
          { name: 'health', status: 'ok', capturedAt: new Date().toISOString(), detail: null },
        ],
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
  if (process.env.PW_BASE_URL) await expect(page).toHaveURL(new RegExp(process.env.PW_BASE_URL.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
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

    const badge = page.getByTestId('orch-context-badge');
    await expect(badge).toBeVisible();
    await expect(badge).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('orch-context-count')).toHaveText('3');
    await expect(page.getByTestId('orch-context-menu')).toHaveCount(0);
    const messageContext = page.getByTestId('orch-message-context');
    await expect(messageContext).toBeVisible();
    await expect(messageContext).toContainText('Next message');
    await expect(page.getByTestId('orch-observed-view')).toContainText(`${PROJECT} board`);
    await expect(page.getByTestId('orch-selected-history')).toContainText(`Project ${PROJECT}`);
    await expect(page.getByTestId('orch-message-context-summary')).toContainText('will be included');
    await expect(page.getByTestId('orch-side-sheet-pin')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet-settings')).toBeVisible();
    await expect(page.getByTestId('orch-side-sheet-refresh')).toBeVisible();

    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      await page.screenshot({ path: path.join(resultsDir, 'orchestrator-context-collapsed--mocked.png'), fullPage: false });
    }

    await badge.click();
    await expect(badge).toHaveAttribute('aria-expanded', 'true');
    const contextMenu = page.getByTestId('orch-context-menu');
    await expect(contextMenu).toBeVisible();
    await expect(page.getByTestId('chat-context-groups')).toBeVisible();

    const sheetBox = await page.getByTestId('orch-side-sheet').boundingBox();
    const menuBox = await contextMenu.boundingBox();
    expect(sheetBox).not.toBeNull();
    expect(menuBox).not.toBeNull();
    expect(Math.abs(menuBox!.x - sheetBox!.x)).toBeLessThanOrEqual(1);
    expect(Math.abs(menuBox!.width - sheetBox!.width)).toBeLessThanOrEqual(2);

    await expect(page.getByTestId('orch-context-freshness')).toContainText('Context captured');
    await expect(page.getByTestId(`chat-switcher-row-project:${PROJECT}`)).toContainText('running');
    await expect(page.getByTestId(`chat-switcher-row-task:${PROJECT}/AGT-1933`)).toContainText('parked');
    await expect(page.getByTestId(`chat-switcher-row-task:${PROJECT}/AGT-1933`)).toContainText('15k');

    if (resultsDir) {
      await page.screenshot({ path: path.join(resultsDir, 'orchestrator-context-expanded--mocked.png'), fullPage: false });
    }

    // History navigation is a separate surface. Close it before exercising
    // the composer-adjacent controls for the next outbound message.
    await badge.click();
    await expect(page.getByTestId('orch-message-context')).toBeVisible();

    const forcedRefresh = page.waitForRequest((request) =>
      request.method() === 'POST'
      && new URL(request.url()).pathname === `/api/orchestrator/context/project:${PROJECT}/refresh`);
    await page.getByTestId('orch-side-sheet-refresh').click();
    await forcedRefresh;
    await expect(page.getByTestId('orch-context-freshness')).toContainText('Context captured');

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
    await expect(pinBtn).toHaveAttribute('aria-pressed', 'true');
    await expect(pinBtn).toContainText('Unpin');
    await expect(page.getByTestId('orch-message-context-summary')).toContainText('(pinned)');
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeDisabled();

    await page.screenshot({
      path: 'screenshots/orchestrator-side-sheet-pin/pinned--mocked.png',
      fullPage: false,
    });

    // Unpin: back to following navigation.
    await pinBtn.click();
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeEnabled();
  });

  for (const colorScheme of ['light', 'dark'] as const) {
    test(`${colorScheme} narrow layout keeps history and next-message scopes distinct`, async ({ page }) => {
      await page.emulateMedia({ colorScheme });
      await page.setViewportSize({ width: 540, height: 820 });
      await stubWorkspace(page);
      await openSideSheet(page);

      const context = page.getByTestId('orch-message-context');
      await expect(context).toBeVisible();
      await expect(context.getByRole('button', { name: 'Pin', exact: true })).toBeVisible();
      await page.getByTestId('orch-context-badge').click();
      await expect(page.getByRole('region', { name: 'Chat histories' })).toBeVisible();
      await page.getByTestId(`chat-switcher-row-task:${PROJECT}/AGT-1933`).getByRole('button').first().click();
      await expect(page.getByTestId('orch-selected-history')).toContainText('Task AGT-1933');
      await expect(page.getByTestId('orch-observed-view')).toContainText(`${PROJECT} board`);

      const toggle = page.getByTestId('orch-context-send-toggle');
      await toggle.focus();
      await page.keyboard.press('Enter');
      await expect(toggle).toHaveAttribute('aria-pressed', 'false');
      await expect(page.getByTestId('orch-message-context-summary')).toContainText('No workspace context');
    });
  }
});
