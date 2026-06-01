import { test, expect, Page } from '@playwright/test';

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
 * `/api/jobs/grouped` so the spec doesn't depend on the watch path
 * carrying real orchestrator activity.
 */

const SHOTS = 'screenshots/token-bubble';

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
    lastModel: string | null;
    lastUpdate: string | null;
    entries: Array<{
      ts: string;
      model: string | null;
      inputTokens: number;
      outputTokens: number;
      cacheReadTokens: number;
      cacheCreationTokens: number;
    }>;
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
    if (p === '/api/tasks/grouped') {
      const body = {
        backlog: [],
        preparation: jobs.filter((j) => j.state === '1-preparation'),
        orchestratorPrep: [],
        needsHumanReview: [],
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
        lastModel: 'claude-sonnet-4-6',
        lastUpdate: '2026-05-05T08:30:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-sonnet-4-6', inputTokens: 50_000, outputTokens: 6_000, cacheReadTokens: 100_000, cacheCreationTokens: 4_000 },
          { ts: '2026-05-05T08:15:00Z', model: 'claude-sonnet-4-6', inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 80_000, cacheCreationTokens: 4_000 },
          { ts: '2026-05-05T08:30:00Z', model: 'claude-sonnet-4-6', inputTokens: 30_000, outputTokens: 6_000, cacheReadTokens: 70_000, cacheCreationTokens: 4_000 }
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
    const popover = card.locator('[data-testid="task-card-token-popover"]');
    await expect(popover).toBeVisible();
    await expect(popover.getByTestId('token-row-input')).toContainText('120k');
    await expect(popover.getByTestId('token-row-output')).toContainText('18k');
    await expect(popover.getByTestId('token-row-cache-read')).toContainText('250k');
    await expect(popover.getByTestId('token-row-cache-write')).toContainText('12k');
    await expect(popover.getByTestId('token-row-total')).toContainText('400k');
    await expect(popover.getByTestId('token-row-model')).toContainText('claude-sonnet-4-6');
    await expect(popover.getByTestId('token-popover-timeline-link')).toBeVisible();

    // Anti-clipping contract: the popover must render in the browser top
    // layer (native Popover API) so the card's overflow:hidden +
    // content-visibility paint containment and the lane scroll container
    // cannot cut it off. `:popover-open` only matches when showPopover()
    // promoted it to the top layer; the bounding box must also sit fully
    // inside the viewport (the directive clamps it at every edge).
    await expect(popover).toHaveAttribute('popover', 'manual');
    expect(await popover.evaluate((el: HTMLElement) => el.matches(':popover-open'))).toBe(true);
    const popBox = await popover.boundingBox();
    const vp = page.viewportSize()!;
    expect(popBox).not.toBeNull();
    expect(popBox!.x).toBeGreaterThanOrEqual(0);
    expect(popBox!.y).toBeGreaterThanOrEqual(0);
    expect(popBox!.x + popBox!.width).toBeLessThanOrEqual(vp.width + 1);
    expect(popBox!.y + popBox!.height).toBeLessThanOrEqual(vp.height + 1);

    // Screenshot evidence: bubble + popover. We screenshot a tight crop so
    // the diff stays small.
    const cardBox = await card.boundingBox();
    if (cardBox) {
      await page.screenshot({
        path: `${SHOTS}/card-with-bubble-and-popover.png`,
        clip: {
          x: Math.max(0, cardBox.x - 8),
          y: Math.max(0, cardBox.y - 8),
          width: Math.min(page.viewportSize()!.width - cardBox.x + 8, cardBox.width + 16),
          height: cardBox.height + 240
        }
      });
    }
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
