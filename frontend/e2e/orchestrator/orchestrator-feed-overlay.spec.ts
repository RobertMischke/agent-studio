import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme, dismissDevErrorDialog, sampleColours, type Theme } from '../helpers/theme';
import { contrastRatio } from '../helpers/contrast';

/**
 * Orchestrator-feed overlay regression (ASS-693 ticket): the overlay that
 * shows the global orchestrator "brain" plus the per-project feed had a
 * broken column layout, dark-only hex colours that washed out on the light
 * shell, no visible separation between the global and project scopes, and
 * no deep-link anchor (a reload/bookmark could not reproduce the open feed).
 *
 * This spec locks the four fixes against a fully mocked backend so it is
 * deterministic and needs no live data:
 *   1. Deep-link: navigating to `#/project/<slug>/feed` reproduces the open
 *      overlay once watch-paths resolve the slug.
 *   2. Scope separation: both the "Global scope" badge and the "Project
 *      scope" chip render and read their distinct labels.
 *   3. Layout: at a wide viewport the three feed panes (filters / stream /
 *      detail) all lay out side-by-side with non-zero width.
 *   4. Contrast: the feed's readable text clears WCAG AA on BOTH the dark
 *      (Mocha) and light shells — the regression that motivated the ticket.
 * Light + dark screenshots are dropped into the job results/ folder.
 */

const SLUG = 'runbook';
const PROJECT = 'Runbook';

const SESSION = {
  sessionId: 'orch-sess-1',
  model: 'claude-opus-4-8',
  bootedAt: '2026-06-04T08:00:00Z',
  bootPromptPreview: 'You are the global orchestrator across all watched projects.',
  bootReplyPreview: 'Understood. Monitoring every watched project for stalls and drift.',
  cumulativeInputTokens: 128_400,
  cumulativeOutputTokens: 22_310,
  cumulativeCacheReadTokens: 510_000,
  cumulativeCacheCreationTokens: 64_000,
  calls: 37,
  lastUsedAt: '2026-06-04T09:42:00Z',
  lastError: null,
};

const LOG_ENTRIES = [
  {
    ts: '2026-06-04T09:10:00Z',
    kind: 'observation',
    topic: 'board/scan',
    summary: 'Scanned 7 lanes; 2 tasks idle in review for >2h.',
    reasoning: null,
    jobId: null,
    tokenUsage: { model: 'claude-haiku-4-5', inputTokens: 1200, outputTokens: 340, cacheReadTokens: 8000, cacheCreationTokens: 0 },
  },
  {
    ts: '2026-06-04T09:20:00Z',
    kind: 'decision',
    topic: 'review/escalate',
    summary: 'Escalate the stalled auth-refactor task to a human reviewer rather than auto-accepting it.',
    reasoning: 'The diff touches session-token storage which legal flagged; auto-accept policy excludes compliance-sensitive paths.',
    jobId: 'task-auth-refactor-42',
    tokenUsage: { model: 'claude-opus-4-8', inputTokens: 9400, outputTokens: 1820, cacheReadTokens: 41000, cacheCreationTokens: 2200 },
  },
  {
    ts: '2026-06-04T09:31:00Z',
    kind: 'action',
    topic: 'followup/queue',
    summary: 'Queued a Steer follow-up asking the agent to add an integration test before re-review.',
    reasoning: null,
    jobId: 'task-auth-refactor-42',
    tokenUsage: { model: 'claude-opus-4-8', inputTokens: 5100, outputTokens: 640, cacheReadTokens: 22000, cacheCreationTokens: 0 },
  },
  {
    ts: '2026-06-04T09:40:00Z',
    kind: 'intervention',
    topic: 'watchdog/kill',
    summary: 'Killed a runaway run that exceeded the per-task token budget twice in a row.',
    reasoning: 'Two consecutive budget overruns with no commit; watchdog policy kills to protect the quota.',
    jobId: 'task-flaky-99',
    tokenUsage: null,
  },
];

const EMPTY_GROUPED = {
  archive: [], autoReview: [], backlog: [], codeNotComplete: [], completed: [],
  humanReview: [], preparation: [], progress: [], ready: [],
};

async function mockBackend(page: Page): Promise<void> {
  // Broad fallback FIRST so it has the lowest precedence; specific routes
  // registered afterwards win. Keeps a backend-less `ng serve` from spraying
  // proxy ECONNREFUSED errors that would pollute the screenshot. Anything
  // that looks like a collection gets `[]`; everything else gets `{}`.
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    const list = /\/(watch-paths|tasks|clients|workspaces|tags|models)\b/.test(url);
    return route.fulfill({ status: 200, contentType: 'application/json', body: list ? '[]' : '{}' });
  });

  // The board's `laneGroups` computed iterates each lane, so the grouped
  // endpoint must return the full lane-keyed shape (empty arrays) or the
  // app throws `jobs is not iterable` and the dev error dialog buries the UI.
  await page.route('**/api/tasks/grouped', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(EMPTY_GROUPED) }),
  );

  await page.route('**/api/watch-paths', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: 'C:/Projects/Runbook', rootPath: 'C:/Projects/Runbook' },
        { name: 'Agent Task Processor', path: 'C:/Projects/agent-taskboard', rootPath: 'C:/Projects/agent-taskboard' },
      ]),
    }),
  );

  // StatusBar.runningCount does `Object.values(status.projects)`, so the
  // runner-status snapshot must carry a `projects` object.
  await page.route('**/api/runner/status', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }),
  );

  await page.route('**/api/runner/global/orchestrator-session', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ project: 'global', session: SESSION }) }),
  );

  await page.route('**/api/runner/*/orchestrator-log', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ project: PROJECT, entries: LOG_ENTRIES }) }),
  );

  await page.route('**/api/runner/*/token-summary', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        orchestratorEntries: LOG_ENTRIES.length,
        orchestratorLlmCalls: 4,
        totalInputTokens: 20_800,
        totalOutputTokens: 3_640,
        totalCacheReadTokens: 71_000,
        totalCacheCreationTokens: 4_400,
        estimatedApiCostUsd: 0.42,
        allModelsPriced: true,
        byModel: [],
        disclaimer: 'Estimated from list prices.',
      }),
    }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'orch-feed-overlay')
  : join(process.cwd(), 'test-results', 'orch-feed-overlay');

test('orchestrator-feed overlay: deep-link opens it, scopes are labelled, layout + contrast hold on both themes', async ({ page }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 960 });
  await mockBackend(page);

  // Requirement #4 — deep-link anchor. A cold navigation straight to the
  // feed hash must reproduce the open overlay (bookmark / reload parity).
  await page.goto(`/#/project/${SLUG}/feed`);

  const feed = page.getByTestId('orchestrator-feed');
  await expect(feed).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await expect(feed).toBeVisible();

  // The URL anchor survives the round-trip.
  expect(await page.evaluate(() => location.hash)).toBe(`#/project/${SLUG}/feed`);

  // Requirement #3 — scope separation is explicit and labelled.
  const projectScope = page.getByTestId('orchestrator-feed-scope');
  const globalScope = page.getByTestId('global-orchestrator-scope');
  await expect(projectScope).toBeVisible();
  await expect(projectScope).toHaveText(/project scope/i);
  await expect(globalScope).toBeVisible();
  await expect(globalScope).toHaveText(/global scope/i);

  // Feed actually rendered the mocked entries.
  const entries = page.locator('.orch-feed__entry');
  await expect(entries.first()).toBeVisible();
  expect(await entries.count()).toBe(LOG_ENTRIES.length);

  // Requirement #1 — three-pane layout lays out side by side (non-zero,
  // left-to-right) at a wide viewport, not collapsed/overlapping.
  const [filters, stream, detail] = await Promise.all([
    page.locator('.orch-feed__filters').boundingBox(),
    page.getByTestId('orchestrator-feed-stream').boundingBox(),
    page.getByTestId('orchestrator-feed-detail').boundingBox(),
  ]);
  if (!filters || !stream || !detail) throw new Error('feed panes missing bounding boxes');
  for (const box of [filters, stream, detail]) expect(box.width).toBeGreaterThan(40);
  expect(stream.x).toBeGreaterThan(filters.x + filters.width - 4);
  expect(detail.x).toBeGreaterThan(stream.x + stream.width - 4);

  // Requirement #2 — contrast on BOTH themes. Sample the readable text the
  // ticket called out and assert it clears WCAG AA, then drop a screenshot.
  const themes: Theme[] = ['dark', 'light'];
  for (const theme of themes) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await page.waitForTimeout(150);

    // (selector, nth, minRatio). Body text -> 4.5; bold uppercase pills are
    // small but decorative scope markers -> 3.0 (still comfortably legible).
    const probes: Array<[string, number, number]> = [
      ['.orch-feed__title', 0, 4.5],
      ['.orch-feed__sub', 0, 4.5],
      ['.orch-feed__refresh', 0, 4.5],
      ['.orch-feed__summary', 0, 4.5],
      ['[data-testid="orchestrator-feed-scope"]', 0, 3.0],
      ['[data-testid="global-orchestrator-scope"]', 0, 3.0],
      ['.global-orch__voice', 0, 4.5],
    ];
    for (const [selector, nth, min] of probes) {
      const { color, bg } = await sampleColours(page, selector, nth);
      const ratio = contrastRatio(color, bg);
      expect(ratio, `${theme} · ${selector} (${color} on ${bg})`).toBeGreaterThanOrEqual(min);
    }

    await page.locator('.overlay__panel--orch-feed').screenshot({
      path: join(RESULTS_DIR, `orch-feed-${theme}.png`),
    });
  }
});
