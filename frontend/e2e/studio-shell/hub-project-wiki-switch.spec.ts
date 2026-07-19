import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * AGT-2023 regression — switching between the Project (Overview) and Wiki
 * sections when the Project-Hub tab is ALREADY open must actually move the
 * rail, not silently do nothing.
 *
 * Root cause fixed: {@link StudioTabStateService.open} keyed a Hub tab on
 * project only (not its section), so re-opening the already-present Hub tab
 * on a different section dropped the new payload and just re-focused the
 * stale one. `open()` now adopts the fresh payload in place, and `openHub()`
 * targets Overview explicitly.
 *
 * This spec is fully MOCKED (no backend): it stubs `/api/**` + `/hubs/**`
 * so it reproduces the surface deterministically and captures labelled
 * `--mocked` screenshots for review. The strict unit assertions live in
 * `studio-tab-state.service.spec.ts` and `project-hub-view.component.spec.ts`.
 *
 * Screenshots land in HUB_SWITCH_SHOT_DIR (the job folder's results/ when
 * set); a local fallback keeps a stand-alone run useful.
 */

const PROJECT = 'Agent Taskboard';

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.HUB_SWITCH_SHOT_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'hub-project-wiki-switch');
})();

function shot(page: Page, name: string) {
  return page.screenshot({ path: path.join(SCREENSHOT_DIR, name), fullPage: true });
}

/** Broadly stub the boot so the studio shell renders one project offline. */
async function installMocks(page: Page): Promise<void> {
  // Register broad fallbacks FIRST; Playwright runs later-registered routes
  // first, so the specific stubs below win over these catch-alls.

  // Everything else: return an empty-but-valid payload. Arrays keep list
  // endpoints happy; the shell renders its empty states around them.
  await page.route('**/api/**', (route: Route) => {
    if (route.request().method() !== 'GET') return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });

  // SignalR negotiate / socket — never let it reach a real backend.
  await page.route('**/hubs/**', (route: Route) => route.abort());

  // The kanban board reads a fully-shaped GroupedJobs payload (every lane an
  // array). An empty `[]` would blow up the board's lane computeds and abort
  // change detection before the sidebar settles.
  const emptyGrouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  };
  await page.route('**/api/tasks/grouped', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(emptyGrouped) }),
  );
  await page.route('**/api/tasks/archive**', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }) }),
  );
  // Runner status is an object keyed by project; the status bar does
  // Object.values(status.projects) and throws on an array/undefined.
  await page.route('**/api/runner/status', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }),
  );
  // The header quota strip reads a QuotaReport object (has `.snapshots`).
  await page.route('**/api/cli/quota', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-01-01T00:00:00Z', snapshots: [], ttlSeconds: 600 }) }),
  );
  await page.route('**/api/cli/usage', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }) }),
  );

  const STORAGE = 'C:/repos/agent-taskboard';
  // The Project Hub Overview panel reads a per-project snapshot and dereferences
  // `snap.settings.autoCommit`; an empty `[]`/`{}` would throw and pop the
  // global error dialog over the surface we're screenshotting.
  await page.route('**/api/projects/*/snapshot', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        capturedAt: '2026-01-01T00:00:00Z',
        paths: { path: STORAGE, rootPath: STORAGE, repositoryPath: STORAGE },
        settings: {
          autoCommit: false, crashRecoveryEnabled: false, autoPushStrategy: 'never',
          runnerMode: null, orchestratorModel: null, laneSortStrategies: {},
        },
        runnerStatus: null,
        orchestratorLogTail: [],
        orchestratorSession: null,
        reviewDecisionsPending: [],
        runnerPendingDecisions: [],
        queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
      }),
    }),
  );
  await page.route('**/api/watch-paths', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: STORAGE, rootPath: STORAGE }]),
    }),
  );

  // A real registry workspace so the project row renders as a normal tree row
  // (a null projectId makes the tree render an inline rename input instead).
  await page.route('**/api/workspaces**', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        {
          id: 'ws1', displayName: 'Default', sortOrder: 0, isDefault: true, color: null,
          createdAt: '2026-01-01T00:00:00Z',
          projects: [
            {
              id: 'proj1', displayName: PROJECT, shortCode: 'AGT', workspaceId: 'ws1',
              color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
              storageLocation: STORAGE, urls: [], archived: false, createdAt: '2026-01-01T00:00:00Z',
            },
          ],
        },
      ]),
    }),
  );
}

async function gotoStudio(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await installMocks(page);
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.tabs.v1'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
}

/** Expand the seeded project so its Board / Hub / Wiki child rows show. */
async function expandProject(page: Page): Promise<void> {
  const hubRow = page.getByTestId(`studio-explorer-project-hub-${PROJECT}`);
  if (!(await hubRow.count()) || !(await hubRow.isVisible())) {
    await page.getByTestId(`studio-explorer-project-${PROJECT}`).first().click();
  }
  await expect(hubRow).toBeVisible({ timeout: 5_000 });
}

const hubRow = (page: Page) => page.getByTestId(`studio-explorer-project-hub-${PROJECT}`);
const wikiRow = (page: Page) => page.getByTestId(`studio-explorer-project-wiki-${PROJECT}`);
const rail = (page: Page, key: string) => page.getByTestId(`project-shell-rail-${key}`);

test.describe('Project ⇄ Wiki switch with the Hub tab already open (AGT-2023)', () => {
  test('Wiki open → click Project → lands on Overview; then → Wiki again', async ({ page }) => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await gotoStudio(page);
    await expandProject(page);

    // 1) Open the Wiki first — Hub tab opens deep-linked to the Wiki rail.
    await wikiRow(page).click();
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
    await expect(wikiRow(page)).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await shot(page, '01-wiki-open--mocked.png');

    // 2) The bug: with the Hub tab already open, clicking "Project Hub" used
    //    to do nothing. It must now move the rail to Overview.
    await hubRow(page).click();
    await expect(hubRow(page)).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(rail(page, 'overview')).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(wikiRow(page)).not.toHaveAttribute('aria-current', 'page');
    await shot(page, '02-clicked-project-now-overview--mocked.png');

    // 3) And the reverse direction: from Overview, "Wiki" moves back.
    await wikiRow(page).click();
    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(wikiRow(page)).toHaveAttribute('aria-current', 'page');
    await shot(page, '03-clicked-wiki-back-to-wiki--mocked.png');

    // No duplicate Hub tab was spun up across all the switching — the Open
    // Tabs list holds exactly one entry keyed on the project's Hub tab.
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}`)).toHaveCount(1);
  });

  test('existing Project Hub plus Explorer Wiki produces two distinct editor tabs', async ({ page }) => {
    await gotoStudio(page);
    await expandProject(page);

    await hubRow(page).click();
    await expect(rail(page, 'overview')).toHaveAttribute('aria-current', 'page');
    await wikiRow(page).click();

    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}`)).toHaveCount(1);
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}:wiki`)).toHaveCount(1);
    await expect(page.getByRole('tab', { name: 'AGT · Overview' })).toHaveCount(1);
    await expect(page.getByRole('tab', { name: 'AGT · Wiki' })).toHaveCount(1);
    await shot(page, '04-hub-and-wiki-separate-tabs--mocked.png');
  });

  test('Hub rail Wiki click retargets the current editor tab in place', async ({ page }) => {
    await gotoStudio(page);
    await expandProject(page);
    await hubRow(page).click();

    await rail(page, 'wiki').click();

    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}`)).toHaveCount(0);
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}:wiki`)).toHaveCount(1);
  });
});
