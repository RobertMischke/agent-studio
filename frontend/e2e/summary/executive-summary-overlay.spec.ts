import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Workspace executive-summary overlay (`#/workspace/summary`).
 *
 * The summary endpoint is folded from many on-disk records (job moves,
 * decision journals, advisories, commits, crash evidence). To get a
 * deterministic, fully-populated render that does not depend on which
 * backend binary is deployed, the spec stubs `/api/workspace/summary`
 * with a rich payload that exercises every section: severity-ranked top
 * decisions, per-project activity with commits + moves, open human
 * decisions, and crash evidence.
 *
 * Coverage:
 *   1. Overlay opens from the deep-link hash and renders the headline.
 *   2. All four content blocks render with the stubbed records.
 *   3. The window toggle re-queries (6 h / 24 h / 7 days).
 */

const SHOT_DIR = process.env.SUMMARY_SHOT_DIR ?? 'test-results';

function buildSummary(windowHours: number) {
  const now = Date.now();
  const iso = (msAgo: number) => new Date(now - msAgo).toISOString();

  return {
    windowStart: iso(windowHours * 60 * 60 * 1000),
    windowEnd: iso(0),
    headline:
      `In the last ${windowHours} hours: 2 projects active, 4 commits, ` +
      `1 job move, 2 advisories, 1 crash record, 1 open human decision.`,
    byProject: [
      {
        project: 'agent-taskboard',
        jobsMoved: [
          {
            jobId: 'protocol-summary-and-executive-summary-schema',
            fromState: '3-progress',
            toState: '5-done',
            at: iso(35 * 60 * 1000),
          },
        ],
        decisionsMade: 2,
        advisoriesRaised: 1,
        commits: [
          {
            sha: 'a'.repeat(40),
            shortSha: 'a1b2c3d',
            subject: 'feat(summary): fold decisions into executive summary',
            author: 'Robert Mischke',
            at: iso(40 * 60 * 1000),
          },
          {
            sha: 'b'.repeat(40),
            shortSha: 'e4f5a6b',
            subject: 'test(summary): lock in decisions round-trip',
            author: 'Robert Mischke',
            at: iso(50 * 60 * 1000),
          },
        ],
      },
      {
        project: 'runbook',
        jobsMoved: [],
        decisionsMade: 1,
        advisoriesRaised: 1,
        commits: [
          {
            sha: 'c'.repeat(40),
            shortSha: '7c8d9e0',
            subject: 'fix(api): tolerate missing decision journal',
            author: 'Robert Mischke',
            at: iso(120 * 60 * 1000),
          },
          {
            sha: 'd'.repeat(40),
            shortSha: '1f2a3b4',
            subject: 'chore: bump runner deps',
            author: 'Robert Mischke',
            at: iso(150 * 60 * 1000),
          },
        ],
      },
    ],
    crashes: [
      {
        at: iso(95 * 60 * 1000),
        kind: 'orphan-recovery',
        path: 'logs/orphan-recoveries.jsonl',
        summary: 'agent-taskboard/stale-job archived without completion sentinel',
      },
    ],
    topDecisions: [
      {
        project: 'agent-taskboard',
        decisionId: 'job-escalate@2026-05-31T11:00:00Z',
        at: iso(20 * 60 * 1000),
        severity: 'High',
        title: 'Escalated to human: review verdict could not be parsed twice',
        jobId: 'job-escalate',
      },
      {
        project: 'runbook',
        decisionId: 'job-reissue@2026-05-31T10:30:00Z',
        at: iso(30 * 60 * 1000),
        severity: 'Warn',
        title: 'Reissued with stronger framing after weak first attempt',
        jobId: 'job-reissue',
      },
      {
        project: 'agent-taskboard',
        decisionId: 'job-accept@2026-05-31T10:00:00Z',
        at: iso(45 * 60 * 1000),
        severity: 'Info',
        title: 'Accepted as done: all four review aspects passed',
        jobId: 'job-accept',
      },
    ],
    openHumanDecisions: [
      {
        project: 'agent-taskboard',
        jobId: 'human-decision-needed-pick-auth-strategy',
        title: 'Pick auth strategy for the public API',
        createdAt: iso(6 * 60 * 60 * 1000),
      },
    ],
    schemaVersion: '1',
  };
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route(/\/api\/jobs(\?.*)?$/, json([]));
  await page.route('**/api/jobs/grouped*', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([
    { name: 'agent-taskboard', path: 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard' },
    { name: 'runbook', path: 'C:/Projects/Runbook' },
  ]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/token-summary-aggregate*', json({
    projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stubbed'
  }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/cli/usage', json({ entries: [] }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/clients', json([]));
}

test.describe('Workspace executive summary overlay', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 980 });
    await stubBackgroundApis(page);
  });

  test('renders headline, top decisions, per-project activity, open decisions and crashes', async ({ page }) => {
    await page.route('**/api/workspace/summary*', async (route) => {
      const url = new URL(route.request().url());
      const windowHours = Number(url.searchParams.get('windowHours') ?? '24');
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(buildSummary(windowHours)),
      });
    });

    await page.goto('http://localhost:4010/#/workspace/summary');
    await page.waitForLoadState('domcontentloaded');

    const overlay = page.getByTestId('workspace-summary-overlay');
    await expect(overlay).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-summary')).toBeVisible();

    await expect(page.getByTestId('wsm-headline')).toContainText('projects active');

    // Top decisions: three rows, High ranked first.
    const decisions = page.getByTestId('wsm-top-decisions').locator('.wsm__decision');
    await expect(decisions).toHaveCount(3);
    await expect(decisions.nth(0).locator('.wsm__sev')).toHaveText('High');

    // Per-project: two project cards.
    await expect(page.getByTestId('wsm-by-project').locator('.wsm__project')).toHaveCount(2);

    // Open human decisions + crashes both present.
    await expect(page.getByTestId('wsm-open-decisions')).toBeVisible();
    await expect(page.getByTestId('wsm-crashes')).toBeVisible();

    await page.screenshot({ path: join(SHOT_DIR, 'executive-summary-overlay.png'), fullPage: false });
  });

  test('window toggle re-queries the endpoint', async ({ page }) => {
    const requested: number[] = [];
    await page.route('**/api/workspace/summary*', async (route) => {
      const url = new URL(route.request().url());
      const windowHours = Number(url.searchParams.get('windowHours') ?? '24');
      requested.push(windowHours);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(buildSummary(windowHours)),
      });
    });

    await page.goto('http://localhost:4010/#/workspace/summary');
    await page.waitForLoadState('domcontentloaded');
    await expect(page.getByTestId('workspace-summary')).toBeVisible({ timeout: 5_000 });

    await page.getByTestId('wsm-win-6h').click();
    await expect.poll(() => requested.includes(6)).toBe(true);

    await page.getByTestId('wsm-win-7d').click();
    await expect.poll(() => requested.includes(168)).toBe(true);

    await page.screenshot({ path: join(SHOT_DIR, 'executive-summary-6h-window.png'), fullPage: false });
  });
});
