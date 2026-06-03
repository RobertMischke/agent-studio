import { test, expect, Page } from '@playwright/test';

/**
 * Multi-aspect auto-review surface (slice 1).
 *
 * Verifies the kanban-side contract introduced when the orchestrator
 * gained a multi-aspect quality pass for jobs in `4-auto-review`:
 *   - the lane header shows a live status string sourced from
 *     `/api/auto-review/status` so the user sees the orchestrator is
 *     alive and forming opinions instead of silent durchwinken;
 *   - an info button next to the lane title opens a drawer that
 *     explains what auto-review does;
 *   - jobs that picked up a `<namespace>:concerns` tag from the
 *     pipeline render a small ⚠ chip on the card.
 *
 * Fully fixture-driven via `page.route` intercepts so the spec doesn't
 * depend on a real running orchestrator.
 */

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
  tokenSummary: null;
  tags: string[];
}

function jobStub(over: Partial<JobInfoStub>): JobInfoStub {
  const id = over.id ?? 'stub-job';
  return {
    id,
    jobKey: `stub::${id}`,
    title: over.title ?? id,
    state: over.state ?? '4-auto-review',
    order: over.order ?? 1,
    agent: 'claude',
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
    tags: over.tags ?? [],
    ...over
  };
}

interface AutoReviewStatus {
  lastTickAt: string | null;
  accept: number;
  reissue: number;
  escalate: number;
  aspectsRun: number;
  pending: number;
  currentJob: string | null;
  currentProject: string | null;
}

async function installMocks(
  page: Page,
  jobs: JobInfoStub[],
  status: AutoReviewStatus
): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/jobs/grouped') {
      const body = {
        backlog: jobs.filter((j) => j.state === '0-backlog'),
        preparation: jobs.filter((j) => j.state === '1-preparation'),
        orchestratorPrep: [],
        ready: jobs.filter((j) => j.state === '2-ready'),
        progress: jobs.filter((j) => j.state === '3-progress'),
        failedPickup: [],
        autoReview: jobs.filter((j) => j.state === '4-auto-review'),
        humanReview: jobs.filter((j) => j.state === '5-human-review'),
        review: jobs.filter((j) => j.state === '4-auto-review'),
        completed: jobs.filter((j) => j.state === '6-completed'),
        archive: jobs.filter((j) => j.state === '7-archive')
      };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    }
    if (p === '/api/jobs' || p === '/api/jobs/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p === '/api/auto-review/status') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(status) });
    }
    if (p.startsWith('/api/concept-docs/')) {
      const topic = p.substring('/api/concept-docs/'.length);
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          topic,
          title: 'Auto-Review',
          body: 'Auto-review runs a multi-aspect quality pass on every job that ends with `[[TASK_DONE]]`. Each aspect writes its own `aspect-*.md` into the job folder.\n\nWhen all aspects pass, the orchestrator promotes the job to human review.'
        })
      });
    }
    if (p === '/api/tags') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) });
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
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

async function dismissErrorDialogIfPresent(page: Page): Promise<void> {
  const overlay = page.locator('app-error-dialog .overlay--error');
  if (await overlay.isVisible().catch(() => false)) {
    const close = page.locator('app-error-dialog button').first();
    await close.click({ trial: false }).catch(() => { /* best-effort */ });
  }
}

test.describe('Auto-review multi-aspect surface', () => {
  test('lane header shows live status string and info drawer opens', async ({ page }) => {
    const job = jobStub({ id: 'fixture-pass', title: 'All aspects pass', state: '4-auto-review', tags: [] });
    await installMocks(page, [job], {
      lastTickAt: new Date(Date.now() - 12_000).toISOString(),
      accept: 4,
      reissue: 1,
      escalate: 0,
      aspectsRun: 16,
      pending: 6,
      currentJob: 'fixture-pass',
      currentProject: 'stub-project'
    });

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    const headerStatus = page.getByTestId('header-auto-review-status');
    await expect(headerStatus).toBeVisible({ timeout: 10_000 });
    await expect(headerStatus).toContainText('Auto-review running');

    // Status string is the load-bearing surface: it carries the per-tick
    // counters and the "last tick was N seconds ago" recency. Without
    // it the user has no way to tell that the orchestrator is alive.
    const statusLine = page.getByTestId('auto-review-status');
    await expect(statusLine).toBeVisible({ timeout: 10_000 });
    await expect(statusLine).toContainText('6 queued');
    await expect(statusLine).toContainText('4 accept');
    await expect(statusLine).toContainText('1 reissue');
    await expect(statusLine).toContainText('0 escalate');

    const card = page.locator('[data-testid="job-card"]', { hasText: 'All aspects pass' });
    await expect(card.getByTestId('job-card-auto-review-status')).toContainText('reviewing now');

    // Info button next to the lane title opens the centered lane-info
    // modal with the rendered concept doc fetched from /api/concept-docs/.
    // The body text comes from docs/concept-docs/lane-4-auto-review.md.
    const infoBtn = page.getByTestId('info-button-lane-4-auto-review');
    await expect(infoBtn).toBeVisible();
    await infoBtn.click();

    const modal = page.getByTestId('info-button-modal-lane-4-auto-review');
    await expect(modal).toBeVisible();
    await expect(modal).toContainText(/multi-aspect/i);
    await expect(modal).toContainText(/aspect-/);

    // Capture lane-header evidence (status string + open modal + info button).
    await page.setViewportSize({ width: 1400, height: 900 });
    await page.screenshot({ path: 'screenshots/auto-review/auto-review-lane-header.png', fullPage: false });

    // Close the modal.
    await page.getByTestId('info-button-modal-lane-4-auto-review-close').click();
    await expect(modal).toHaveCount(0);
  });

  test('job with quality:concerns tag renders a ⚠ concern chip', async ({ page }) => {
    const job = jobStub({
      id: 'fixture-concerns',
      title: 'Auto-review flagged concerns',
      state: '4-auto-review',
      tags: ['quality:concerns', 'docs:concerns']
    });
    await installMocks(page, [job], {
      lastTickAt: new Date(Date.now() - 5_000).toISOString(),
      accept: 1,
      reissue: 0,
      escalate: 0,
      aspectsRun: 4,
      pending: 1,
      currentJob: null,
      currentProject: null
    });

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    const card = page.locator('[data-testid="job-card"]', { hasText: 'Auto-review flagged concerns' });
    await expect(card).toBeVisible({ timeout: 10_000 });
    await expect(card.getByTestId('job-card-auto-review-status')).toContainText('queued for review');

    const concernChips = card.locator('[data-testid="job-card-concern-chip"]');
    await expect(concernChips).toHaveCount(2);

    // First chip is the warning glyph plus the namespaced label.
    const first = concernChips.first();
    await expect(first).toContainText('⚠');
    await expect(first).toContainText('quality:concerns');

    // Second chip from the docs aspect.
    await expect(concernChips.nth(1)).toContainText('docs:concerns');

    // Capture concern-chip evidence on the card.
    await page.setViewportSize({ width: 1400, height: 900 });
    await card.screenshot({ path: 'screenshots/auto-review/auto-review-concerns-card.png' });
  });
});
