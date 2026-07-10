import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog } from '../helpers/theme';

/**
 * Global Workspace-settings home ("Dach") - ASS-695.
 *
 * The formerly scattered workspace overlays (CLI usage caps, token
 * timeline, visual-evidence reel, executive summary) now live with
 * system prompts as sections of one rail+panel home, mirroring the
 * project-level settings layout. The status bar exposes a single
 * "Settings" entry instead of three separate buttons; deep-links still
 * resolve to the right section.
 *
 * This spec stubs every backend route the sections poll so it runs
 * against a clean dev frontend with no backend up. It asserts:
 *   - The status-bar "Settings" button opens the home on the overview.
 *   - The overview lists a card per content section.
 *   - The rail navigates between sections and each section keeps its
 *     legacy outer test id + inner component (so old deep-links/specs
 *     still resolve).
 *   - A `#/workspace/settings` deep-link opens the home directly.
 *   - The close button dismisses the home.
 */

const SHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? 'test-results';

function settingsHome(page: Page) {
  return page.locator(
    '[data-testid="workspace-settings-inline"], [data-testid="workspace-settings-overlay"]',
  );
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/tasks', json([]));
  await page.route('**/api/tasks/grouped', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/token-summary-aggregate*', json({
    projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stubbed',
  }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/cli/quota/caps', json({ defaultCapPct: 95, caps: {} }));
  // The CLI-admin (Usage caps) section pulls model-route profiles via the quota
  // feature. Stub it too, else an unstubbed call falls through to the dev
  // server's SPA index.html and the app's generic HTTP handler pops an error
  // dialog (only reachable with no backend up).
  await page.route('**/api/cli/quota/model-routes', json({ profiles: {} }));
  await page.route('**/api/cli/usage', json({ entries: [] }));
  await page.route('**/api/cli/contracts', json([]));
  await page.route('**/api/cli/*/models*', json({ models: [], source: 'stubbed' }));
  await page.route('**/api/cli/*/working-memory*', json({ entries: [] }));
  await page.route('**/api/admin/prompts', json({
    overrideDirectory: 'stubbed',
    items: [
      {
        name: 'runner-fresh-start.md',
        title: 'Runner: fresh start',
        description: 'Bootstrap prompt handed to the CLI agent when a task starts from scratch.',
        group: 'Runner',
        hasDefault: true,
        hasOverride: false,
        defaultChangedSinceOverride: false,
        slots: [],
        usageCount: 0,
      },
    ],
  }));
  await page.route('**/api/admin/prompts/coverage', json({
    items: [], totalSites: 0, coveredSites: 0, pendingSites: 0,
  }));
  await page.route('**/api/admin/prompts/runner-fresh-start.md', json({
    name: 'runner-fresh-start.md',
    title: 'Runner: fresh start',
    description: 'Bootstrap prompt handed to the CLI agent when a task starts from scratch.',
    group: 'Runner',
    hasDefault: true,
    hasOverride: false,
    defaultContent: 'Default prompt',
    overrideContent: null,
    baseDefaultContent: null,
    effectiveContent: 'Default prompt',
    defaultSha: '0123456789abcdef',
    baseDefaultSha: null,
    defaultChangedSinceOverride: false,
    overrideUpdatedAt: null,
    slots: [],
    usages: [],
  }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/workspace/tokens/timeline*', json({
    windowStart: new Date(Date.now() - 24 * 3600 * 1000).toISOString(),
    windowEnd: new Date().toISOString(),
    windowHours: 24, bucketMinutes: 60, bucketCount: 0,
    cells: [], projects: [], fetchedAt: new Date().toISOString(), disclaimer: 'stubbed',
  }));
  await page.route('**/api/workspace/tokens/expensive-jobs*', json({ jobs: [] }));
  await page.route('**/api/workspace/screenshots*', json({ windowHours: 72, projectFilter: null, screenshots: [] }));
  await page.route('**/api/workspaces*', json([]));
  await page.route('**/api/cli/working-memory*', json({ available: false, root: null, capturedAt: new Date().toISOString(), entries: [] }));
  await page.route('**/api/cli/sessions*', json({ sessions: [] }));
  await page.route('**/api/cli/models*', json({ types: [] }));
}

// AGT-2035 — the consolidated Settings view. Summary was removed; Appearance,
// Updates, Workspaces (Global) and Working memory (Workspace) were added. Each
// section keeps a stable outer overlay test id + an inner component test id.
const SECTIONS: { key: string; overlayTestid: string; innerTestid: string }[] = [
  { key: 'appearance', overlayTestid: 'workspace-appearance-overlay', innerTestid: 'appearance-settings' },
  { key: 'updates', overlayTestid: 'workspace-updates-overlay', innerTestid: 'updates-settings' },
  { key: 'workspaces', overlayTestid: 'workspace-management-overlay', innerTestid: 'workspace-management' },
  { key: 'caps', overlayTestid: 'cli-admin-overlay', innerTestid: 'cli-admin-panel' },
  { key: 'working-memory', overlayTestid: 'workspace-working-memory-overlay', innerTestid: 'workspace-working-memory' },
  { key: 'prompts', overlayTestid: 'prompt-admin-overlay', innerTestid: 'prompt-admin-panel' },
  { key: 'tokens', overlayTestid: 'workspace-tokens-overlay', innerTestid: 'token-usage-section' },
  { key: 'screenshots', overlayTestid: 'workspace-screenshots-overlay', innerTestid: 'workspace-screenshots' },
];

test.describe('Workspace settings home (Dach)', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 950 });
    // Force the legacy (modal) layout so this spec exercises the modal-backed
    // `workspace-settings-overlay` path; the studio (vsCode) layout renders the
    // same view as an editor tab and is covered by settings-consolidation.spec.
    await page.addInitScript(() => { try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* ignore */ } });
    await stubBackgroundApis(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);
    await dismissDevErrorDialog(page);
  });

  test('Settings button opens the home; overview lists every section', async ({ page }) => {
    const trigger = page.getByTestId('status-bar-settings');
    await expect(trigger).toBeVisible();
    await trigger.click();

    await expect(settingsHome(page)).toBeVisible();
    await expect(page.getByTestId('workspace-settings-title')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-overview')).toBeVisible();

    for (const { key } of SECTIONS) {
      await expect(page.getByTestId(`workspace-settings-card-${key}`)).toBeVisible();
      await expect(page.getByTestId(`workspace-settings-rail-${key}`)).toBeVisible();
    }
    // AGT-2035: the consolidated rail labels this section "Usage caps" (the
    // operator's directive-9 name); the CLI-admin panel it hosts is what used to
    // be called "CLI Management". Assert the shipped rail label.
    await expect(page.getByTestId('workspace-settings-rail-caps')).toContainText('Usage caps');

    await page.screenshot({ path: join(SHOT_DIR, 'workspace-settings-overview--mocked.png'), fullPage: false });
  });

  test('rail navigates between sections, each keeping its legacy hook', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(settingsHome(page)).toBeVisible();

    for (const { key, overlayTestid, innerTestid } of SECTIONS) {
      // A dev-only global error dialog (NG0919 under `ng serve`) can paint over
      // the rail and intercept the click; brush it aside before navigating.
      await dismissDevErrorDialog(page);
      await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"]').forEach((n) => n.remove()));
      await page.getByTestId(`workspace-settings-rail-${key}`).click();
      await expect(page.getByTestId(overlayTestid)).toBeVisible();
      await expect(page.getByTestId(innerTestid)).toBeVisible({ timeout: 5_000 });
      await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    }

    await page.getByTestId('workspace-settings-rail-caps').click();
    await page.screenshot({ path: join(SHOT_DIR, 'workspace-settings-cli-management--mocked.png'), fullPage: false });
  });

  test('overview card jumps straight into its section', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await page.getByTestId('workspace-settings-card-tokens').click();
    await expect(page.getByTestId('workspace-tokens-overlay')).toBeVisible();
    await expect(page.getByTestId('workspace-token-timeline')).toBeVisible({ timeout: 5_000 });
  });

  test('deep-link #/workspace/settings opens the home on the overview', async ({ page }) => {
    await page.goto('/#/workspace/settings');
    await page.waitForLoadState('domcontentloaded');
    await expect(settingsHome(page)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-overview')).toBeVisible();
  });

  test('CLI-management deep-link opens the consolidated Settings section', async ({ page }) => {
    await page.goto('/#/workspace/settings/caps');
    await page.waitForLoadState('domcontentloaded');

    await expect(settingsHome(page)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-rail-caps')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('cli-admin-panel')).toBeVisible();
    await expect(page.getByTestId('cli-contracts-explainer')).toContainText('not configuration');
  });

  test('legacy modal close button dismisses the home when modal layout is active', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(settingsHome(page)).toBeVisible();
    test.skip(await page.getByTestId('workspace-settings-close').count() === 0, 'Studio layout renders Settings inline');
    await page.getByTestId('workspace-settings-close').click();
    await expect(page.getByTestId('workspace-settings-overlay')).toHaveCount(0);
  });
});
