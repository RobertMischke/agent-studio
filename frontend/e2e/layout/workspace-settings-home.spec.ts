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
  await page.route('**/api/cli/usage', json({ entries: [] }));
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
      },
    ],
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
  }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/workspace/tokens/timeline*', json({
    windowStart: new Date(Date.now() - 24 * 3600 * 1000).toISOString(),
    windowEnd: new Date().toISOString(),
    windowHours: 24, bucketMinutes: 60, bucketCount: 0,
    cells: [], projects: [], fetchedAt: new Date().toISOString(), disclaimer: 'stubbed',
  }));
  await page.route('**/api/workspace/screenshots*', json({ windowHours: 72, projectFilter: null, screenshots: [] }));
  await page.route('**/api/workspace/summary*', json({
    windowStart: new Date(Date.now() - 24 * 3600 * 1000).toISOString(),
    windowEnd: new Date().toISOString(),
    headline: 'Nothing notable in the last 24 hours.',
    byProject: [], topDecisions: [], openHumanDecisions: [], crashEvidence: [],
  }));
}

const SECTIONS: { key: string; overlayTestid: string; innerTestid: string }[] = [
  { key: 'caps', overlayTestid: 'cli-admin-overlay', innerTestid: 'cli-admin-panel' },
  { key: 'prompts', overlayTestid: 'prompt-admin-overlay', innerTestid: 'prompt-admin-panel' },
  { key: 'tokens', overlayTestid: 'workspace-tokens-overlay', innerTestid: 'workspace-token-timeline' },
  { key: 'screenshots', overlayTestid: 'workspace-screenshots-overlay', innerTestid: 'workspace-screenshots' },
  { key: 'summary', overlayTestid: 'workspace-summary-overlay', innerTestid: 'workspace-summary' },
];

test.describe('Workspace settings home (Dach)', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 950 });
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

    await expect(page.getByTestId('workspace-settings-overlay')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-title')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-overview')).toBeVisible();

    for (const { key } of SECTIONS) {
      await expect(page.getByTestId(`workspace-settings-card-${key}`)).toBeVisible();
      await expect(page.getByTestId(`workspace-settings-rail-${key}`)).toBeVisible();
    }

    await page.screenshot({ path: join(SHOT_DIR, 'workspace-settings-overview.png'), fullPage: false });
  });

  test('rail navigates between sections, each keeping its legacy hook', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(page.getByTestId('workspace-settings-overlay')).toBeVisible();

    for (const { key, overlayTestid, innerTestid } of SECTIONS) {
      await page.getByTestId(`workspace-settings-rail-${key}`).click();
      await expect(page.getByTestId(overlayTestid)).toBeVisible();
      await expect(page.getByTestId(innerTestid)).toBeVisible({ timeout: 5_000 });
    }

    await page.screenshot({ path: join(SHOT_DIR, 'workspace-settings-caps-section.png'), fullPage: false });
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
    await expect(page.getByTestId('workspace-settings-overlay')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-overview')).toBeVisible();
  });

  test('close button dismisses the home', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(page.getByTestId('workspace-settings-overlay')).toBeVisible();
    await page.getByTestId('workspace-settings-close').click();
    await expect(page.getByTestId('workspace-settings-overlay')).toHaveCount(0);
  });
});
