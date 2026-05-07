import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from './helpers/api';

/**
 * Selective info-button on lane headers.
 *
 * The `<app-info-button>` is opt-in per lane: only `4-auto-review` and
 * `3-progress` carry one because their semantics are non-obvious; the
 * other lanes (Backlog / Ready / Done / Archive) deliberately have
 * nothing. The button fetches the rendered concept-doc body from
 * `GET /api/concept-docs/{topic}` (single source of truth in
 * `docs/concept-docs/`) and shows it in a side-drawer.
 *
 * This spec uses `page.route` intercepts so it doesn't require a real
 * job pipeline; the live backend is still reached for /api/concept-docs
 * to prove the endpoint serves the committed markdown.
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
    tags: over.tags ?? []
  };
}

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.INFO_BUTTON_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', 'playwright-screenshots', 'info-button');
})();

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

async function installMocks(page: Page, jobs: JobInfoStub[]): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;

    // Reach the real backend for the concept-docs endpoint so the spec
    // exercises the full pipe (committed markdown -> service -> drawer).
    if (p.startsWith('/api/concept-docs/')) {
      const upstream = await fetch(`${BACKEND}${p}`);
      const body = await upstream.text();
      return route.fulfill({
        status: upstream.status,
        contentType: upstream.headers.get('content-type') ?? 'application/json',
        body
      });
    }

    if (p === '/api/jobs/grouped') {
      const body = {
        backlog: jobs.filter(j => j.state === '0-backlog'),
        preparation: jobs.filter(j => j.state === '1-preparation'),
        orchestratorPrep: [],
        needsHumanReview: [],
        ready: jobs.filter(j => j.state === '2-ready'),
        progress: jobs.filter(j => j.state === '3-progress'),
        failedPickup: [],
        autoReview: jobs.filter(j => j.state === '4-auto-review'),
        humanReview: jobs.filter(j => j.state === '5-human-review'),
        review: jobs.filter(j => j.state === '4-auto-review'),
        completed: jobs.filter(j => j.state === '6-completed'),
        archive: jobs.filter(j => j.state === '7-archive')
      };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    }
    if (p === '/api/jobs' || p === '/api/jobs/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p === '/api/auto-review/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          lastTickAt: new Date(Date.now() - 5_000).toISOString(),
          accept: 0, reissue: 0, escalate: 0, aspectsRun: 0,
          currentJob: null, currentProject: null
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
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([{ name: 'stub-project', path: 'C:/stub', rootPath: 'C:/stub' }])
      });
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

test.describe('Info button on lane headers (selective placement)', () => {
  test('backend serves the committed concept-docs', async () => {
    const a = await api<{ topic: string; title: string; body: string }>('/api/concept-docs/lane-4-auto-review');
    expect(a.topic).toBe('lane-4-auto-review');
    expect(a.title).toBe('Auto-Review');
    expect(a.body.length).toBeGreaterThan(200);

    const b = await api<{ topic: string; title: string; body: string }>('/api/concept-docs/lane-3-progress');
    expect(b.topic).toBe('lane-3-progress');
    expect(b.body.length).toBeGreaterThan(200);
  });

  test('endpoint returns 404 for unknown topics', async () => {
    const res = await fetch(`${BACKEND}/api/concept-docs/nonexistent`);
    expect(res.status).toBe(404);
  });

  test('4-auto-review lane header carries an info button that opens the drawer', async ({ page }) => {
    await installMocks(page, [
      jobStub({ id: 'fix-a', title: 'A', state: '4-auto-review' }),
      jobStub({ id: 'fix-b', title: 'B', state: '3-progress' })
    ]);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const trigger = page.getByTestId('info-button-lane-4-auto-review');
    await expect(trigger).toBeVisible({ timeout: 10_000 });
    await trigger.click();

    const drawer = page.getByTestId('info-button-drawer-lane-4-auto-review');
    await expect(drawer).toBeVisible();
    await expect(page.getByTestId('info-button-title-lane-4-auto-review')).toHaveText('Auto-Review');
    const body = page.getByTestId('info-button-body-lane-4-auto-review');
    await expect(body).toContainText(/multi-aspect/i);

    await page.setViewportSize({ width: 1400, height: 900 });
    await page.screenshot({ path: `${SCREENSHOT_DIR}/01-auto-review-drawer.png`, fullPage: false });

    // ESC closes the drawer.
    await page.keyboard.press('Escape');
    await expect(drawer).toHaveCount(0);
  });

  test('3-progress lane header carries its own info button', async ({ page }) => {
    await installMocks(page, [
      jobStub({ id: 'fix-c', title: 'C', state: '3-progress' })
    ]);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const trigger = page.getByTestId('info-button-lane-3-progress');
    await expect(trigger).toBeVisible({ timeout: 10_000 });
    await trigger.click();

    const drawer = page.getByTestId('info-button-drawer-lane-3-progress');
    await expect(drawer).toBeVisible();
    await expect(page.getByTestId('info-button-title-lane-3-progress')).toContainText(/progress/i);
    const body = page.getByTestId('info-button-body-lane-3-progress');
    await expect(body).toContainText(/orchestrator/i);

    await page.setViewportSize({ width: 1400, height: 900 });
    await page.screenshot({ path: `${SCREENSHOT_DIR}/02-progress-drawer.png`, fullPage: false });

    await page.getByTestId('info-button-close-lane-3-progress').click();
    await expect(drawer).toHaveCount(0);
  });

  test('Backlog / Ready / Done / Archive headers carry NO info button', async ({ page }) => {
    await installMocks(page, [
      jobStub({ id: 'fix-d', title: 'D', state: '2-ready' }),
      jobStub({ id: 'fix-e', title: 'E', state: '6-completed' }),
      jobStub({ id: 'fix-f', title: 'F', state: '7-archive' })
    ]);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    // The kanban renders eagerly, so once any lane is visible we can
    // assert across siblings without polling.
    await expect(page.getByTestId('lane-2-ready')).toBeVisible({ timeout: 10_000 });
    for (const state of ['0-backlog', '1-preparation', '2-ready', '5-human-review', '6-completed', '7-archive']) {
      await expect(page.getByTestId(`info-button-lane-${state}`)).toHaveCount(0);
    }
  });
});
