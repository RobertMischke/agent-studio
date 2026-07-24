import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';

/**
 * Job-card token bubble.
 *
 * Verifies the user-visible contract introduced when the KB size on every
 * card was replaced with a token-spend bubble:
 *   - cards without recorded token activity render no bubble;
 *   - cards with token activity render a colour-tiered bubble showing the
 *     compact total ("2.4k", "850k", "3.1M");
 *   - hovering the bubble reveals a popover with the full breakdown
 *     (input / output / cacheRead / cacheWrite / total / model / last
 *     update) plus a "View workspace timeline" link.
 *
 * The card data is shaped via a `page.route` intercept on
 * `/api/tasks/grouped` so the spec doesn't depend on the watch path
 * carrying real orchestrator activity.
 */

const SHOTS = process.env.JOB_RESULTS_DIR
  ? `${process.env.JOB_RESULTS_DIR}/token-popover-model`
  : 'screenshots/token-bubble';

interface JobInfoStub {
  id: string;
  jobKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: null;
  model: string | null;
  cliType: string | null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  ownerClientId: string;
  tokenSummary: null | {
    calls: number;
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
    totalTokens: number;
    estimatedApiCostUsd?: number;
    allModelsPriced?: boolean;
    lastModel: string | null;
    lastUpdate: string | null;
    entries: {
      ts: string;
      model: string | null;
      inputTokens: number;
      outputTokens: number;
      cacheReadTokens: number;
      cacheCreationTokens: number;
    }[];
  };
}

function jobStub(over: Partial<JobInfoStub>): JobInfoStub {
  const id = over.id ?? 'stub-job';
  return {
    id,
    jobKey: `stub::${id}`,
    title: over.title ?? id,
    state: over.state ?? '2-ready',
    order: over.order ?? 1,
    agent: 'copilot',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: 'C:/stub',
    projectName: 'stub-project',
    folderPath: 'C:/stub/' + id,
    lastActivity: '2026-05-05T08:00:00Z',
    sessionName: null,
    model: 'claude-sonnet-4-6',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ownerClientId: 'local-default',
    tokenSummary: null,
    ...over
  };
}

/**
 * Dismiss the global error dialog if it is mounted. Some startup API
 * calls return shapes the catch-all stub can't perfectly mimic; the app
 * surfaces this as a non-fatal toast that intercepts pointer events. We
 * close it so it can't block the hover test.
 */
async function dismissErrorDialogIfPresent(page: Page): Promise<void> {
  const overlay = page.locator('app-error-dialog .overlay--error');
  if (await overlay.isVisible().catch(() => false)) {
    const close = page.locator('app-error-dialog button').first();
    await close.click({ trial: false }).catch(() => { /* best-effort */ });
  }
}

async function stubGroupedJobs(page: Page, jobs: JobInfoStub[]): Promise<void> {
  // Single route handler that dispatches by URL. Avoids order-of-registration
  // pitfalls (Playwright matches routes in reverse insertion order, so
  // overlapping globs are easy to get wrong). Returns a shape each service
  // can safely consume so the error-dialog overlay never appears and
  // doesn't block pointer events on the bubble.
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/auth/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: false, user: null }),
      });
    }
    if (p === '/api/tasks/archive') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50, hasMore: false }),
      });
    }
    if (p === '/api/crash-recovery/pending') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ pending: [] }),
      });
    }
    if (p === '/api/orchestrator/sessions') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ sessions: [] }),
      });
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(p)) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ models: [], source: 'fixture', fetchedAt: '2026-05-05T08:00:00Z' }),
      });
    }
    if (p === '/api/clients/local-default/defaults') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ clientId: 'local-default', defaultCliType: null, defaultModel: null }),
      });
    }
    if (p === '/api/environment') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
      });
    }
    if (p === '/api/projects/settings') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    }
    if (p === '/api/tasks/grouped') {
      const body = {
        backlog: [],
        preparation: jobs.filter((j) => j.state === '1-preparation'),
        orchestratorPrep: [],
        ready: jobs.filter((j) => j.state === '2-ready'),
        progress: jobs.filter((j) => j.state === '3-progress'),
        failedPickup: [],
        review: jobs.filter((j) => j.state === '4-review'),
        autoReview: [],
        humanReview: [],
        completed: jobs.filter((j) => j.state === '5-completed'),
        archive: jobs.filter((j) => j.state === '6-archive')
      };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    }
    if (p === '/api/tasks' || p === '/api/tasks/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p.startsWith('/api/clients')) {
      const list = [{
        id: 'local-default', displayName: 'Local Default', emoji: '🤖', colour: '#64748b', kind: 'human',
        registeredAt: '2026-01-01T00:00:00Z', lastSeenAt: null, tokenBudgetMonthly: null, notes: null
      }];
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(list) });
    }
    if (p === '/api/watch-paths') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
        { name: 'stub-project', path: 'C:/stub', rootPath: 'C:/stub' }
      ]) });
    }
    if (p === '/api/cli/quota') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }) });
    }
    if (p === '/api/cli/usage') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), sections: [] }) });
    }
    if (p.startsWith('/api/runner')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) });
    }
    // Catch-all: empty array works for list endpoints, empty object for
    // single-record. Use null to cover both shapes safely; consumers
    // should fall back to defaults when the response is empty.
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

test.describe('Token bubble on job cards', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }));
    });
  });

  test('cards without token activity render no bubble', async ({ page }) => {
    const quietJob = jobStub({ id: 'quiet-card', title: 'Quiet card no tokens', tokenSummary: null });
    await stubGroupedJobs(page, [quietJob]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const card = page.locator('[data-testid="task-card"]', { hasText: 'Quiet card no tokens' });
    await expect(card).toBeVisible();
    await expect(card.locator('[data-testid="task-card-token-bubble"]')).toHaveCount(0);
  });

  test('card with tokens shows a bubble; hover reveals the popover', async ({ page }) => {
    const noisyJob = jobStub({
      id: 'noisy-card',
      title: 'Noisy card with tokens',
      tokenSummary: {
        calls: 3,
        inputTokens: 120_000,
        outputTokens: 18_000,
        cacheReadTokens: 250_000,
        cacheCreationTokens: 12_000,
        totalTokens: 400_000,
        estimatedApiCostUsd: 1.25,
        allModelsPriced: true,
        lastModel: 'GPT-5 Codex',
        lastUpdate: '2026-05-05T08:30:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'GPT-5 Codex', inputTokens: 50_000, outputTokens: 6_000, cacheReadTokens: 100_000, cacheCreationTokens: 4_000 },
          { ts: '2026-05-05T08:15:00Z', model: 'Claude Haiku 4.5', inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 80_000, cacheCreationTokens: 4_000 },
          { ts: '2026-05-05T08:30:00Z', model: 'GPT-5 Codex', inputTokens: 30_000, outputTokens: 6_000, cacheReadTokens: 70_000, cacheCreationTokens: 4_000 }
        ]
      }
    });
    await stubGroupedJobs(page, [noisyJob]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const card = page.locator('[data-testid="task-card"]', { hasText: 'Noisy card with tokens' });
    await expect(card).toBeVisible();
    await dismissErrorDialogIfPresent(page);

    const bubble = card.locator('[data-testid="task-card-token-bubble"]');
    await expect(bubble).toBeVisible();
    // 400k total -> blue tier (50k <= total < 500k).
    await expect(bubble).toHaveAttribute('data-token-tier', 'blue');
    // Compact label.
    await expect(bubble).toHaveText('400k');

    // Focusing the bubble reveals the popover (focusin on the wrap drives
    // TokenPopoverDirective). Equivalent to hover for the user-visible
    // contract — the popover is keyboard reachable as well — and dodges
    // pointer-events races with any overlay that might appear before the
    // cards land.
    await bubble.focus();
    const popover = page.locator('[data-testid="task-card-token-popover"]');
    await expect(popover).toBeVisible();
    await expect(popover.getByTestId('token-row-input')).toContainText('120k');
    await expect(popover.getByTestId('token-row-output')).toContainText('18k');
    await expect(popover.getByTestId('token-row-cache-read')).toContainText('250k');
    await expect(popover.getByTestId('token-row-cache-write')).toContainText('12k');
    await expect(popover.getByTestId('token-row-total')).toContainText('400k');
    await expect(popover.getByTestId('token-row-model')).toContainText('GPT-5 Codex');
    await expect(popover.getByTestId('token-cost-tooltip')).toContainText('Estimated cost: $1.25');
    await expect(popover.getByTestId('token-cost-tooltip')).toContainText('Estimated - historical list prices');
    await expect(popover.locator('.task-card__token-table--runs')).toContainText('Claude Haiku 4.5');
    await expect(popover.getByTestId('token-popover-timeline-link')).toBeVisible();

    // Anti-clipping contract: the directive lifts the panel into the
    // shared body overlay portal, so the card's overflow/content-visibility
    // containment and lane scroll container cannot cut it off.
    const popBox = await popover.boundingBox();
    const vp = page.viewportSize()!;
    expect(popBox).not.toBeNull();
    expect(popBox!.x).toBeGreaterThanOrEqual(0);
    expect(popBox!.y).toBeGreaterThanOrEqual(0);
    expect(popBox!.x + popBox!.width).toBeLessThanOrEqual(vp.width + 1);
    expect(popBox!.y + popBox!.height).toBeLessThanOrEqual(vp.height + 1);

    // Screenshot evidence: bubble + overlay popover in the viewport.
    mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({ path: `${SHOTS}/card-with-bubble-and-popover.png`, fullPage: false });
  });

  test('tier escalates with spend', async ({ page }) => {
    const small = jobStub({
      id: 'small-spend',
      title: 'Small spend card',
      order: 1,
      tokenSummary: {
        calls: 1,
        inputTokens: 1_000,
        outputTokens: 500,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 1_500,
        lastModel: 'claude-haiku-4-5',
        lastUpdate: '2026-05-05T08:00:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-haiku-4-5', inputTokens: 1_000, outputTokens: 500, cacheReadTokens: 0, cacheCreationTokens: 0 }
        ]
      }
    });
    const huge = jobStub({
      id: 'huge-spend',
      title: 'Huge spend card',
      order: 2,
      tokenSummary: {
        calls: 1,
        inputTokens: 3_000_000,
        outputTokens: 200_000,
        cacheReadTokens: 3_000_000,
        cacheCreationTokens: 100_000,
        totalTokens: 6_300_000,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-05T08:00:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-opus-4-7', inputTokens: 3_000_000, outputTokens: 200_000, cacheReadTokens: 3_000_000, cacheCreationTokens: 100_000 }
        ]
      }
    });
    await stubGroupedJobs(page, [small, huge]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const smallBubble = page.locator('[data-testid="task-card"]', { hasText: 'Small spend card' })
      .locator('[data-testid="task-card-token-bubble"]');
    await expect(smallBubble).toHaveAttribute('data-token-tier', 'neutral');
    await expect(smallBubble).toHaveText('1.5k');

    const hugeBubble = page.locator('[data-testid="task-card"]', { hasText: 'Huge spend card' })
      .locator('[data-testid="task-card-token-bubble"]');
    await expect(hugeBubble).toHaveAttribute('data-token-tier', 'peach');
    await expect(hugeBubble).toHaveText('6.3M');
  });
});
