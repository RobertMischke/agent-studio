import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs, getJob, moveJob } from '../helpers/jobs';

/**
 * Backlog-lane + task-types + tag-registry slice (slice A + B + C of the
 * backlog-lane-task-types-and-tags task). Covers the spec's required
 * Playwright path: create-bug-with-tag, lands-in-backlog, filter-bar
 * narrows, promote-to-preparation, tag-on-card.
 *
 * The spec is fixture-driven: every job created here is marked
 * `fixture: true` so it is hidden from the default kanban response on
 * stable; `?includeFixtures=true` exposes our own jobs to assertions.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

async function cleanup(prefix: string, watchPath: string): Promise<void> {
  const all = await api<Array<{ id: string; watchPath: string }>>('/api/tasks?includeFixtures=true');
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

test.describe('Backlog lane + task types + tags', () => {
  const PREFIX = 'e2e-backlog-';

  test.beforeAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(PREFIX, wp.path);
  });

  test.afterAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(PREFIX, wp.path);
  });

  test('GET /api/tags seeds the seven default tags on first read', async () => {
    const tags = await api<Array<{ id: string; label: string; color: string; description: string }>>('/api/tags');
    const ids = tags.map(t => t.id);
    expect(ids).toEqual(expect.arrayContaining([
      'ui-ux', 'performance', 'quality', 'architecture', 'security', 'docs', 'observability'
    ]));
    // Each seed entry must carry a non-empty description so the UI can show
    // the wofür hint on hover and in the registry manager.
    for (const id of ['ui-ux', 'performance', 'quality', 'architecture', 'security', 'docs', 'observability']) {
      const entry = tags.find(t => t.id === id);
      expect(entry, `seed entry ${id}`).toBeDefined();
      expect(entry!.description.trim().length).toBeGreaterThan(0);
    }
  });

  test('a new job created without targetState lands in 0-backlog', async () => {
    const wp = await firstWatchPath();
    const created = await createJob({
      id: PREFIX + 'no-target',
      title: 'Backlog landing test',
      watchPath: wp.path,
      // Override the helper's default '2-ready' to leave targetState absent.
      // The /jobs helper sets it to '2-ready'; we hit the endpoint directly.
    });
    // Ignore the helper response; reset the job we just created and re-create
    // without a targetState to assert the spec contract.
    await deleteJob(created.id, wp.path);

    const res = await fetch(`${BACKEND}/api/tasks`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        id: PREFIX + 'no-target',
        title: 'Backlog landing test',
        watchPath: wp.path,
        agent: 'claude',
        cliType: 'claude',
        taskType: 'bug',
        tags: ['architecture'],
        fixture: true
      })
    });
    expect(res.status).toBe(200);

    const job = await getJob(PREFIX + 'no-target', wp.path);
    expect(job.state).toBe('0-backlog');
  });

  test('explicit targetState=2-ready still lands in Ready', async () => {
    const wp = await firstWatchPath();
    const created = await createJob({
      id: PREFIX + 'go-ready',
      title: 'Direct-to-ready shortcut',
      watchPath: wp.path,
      targetState: '2-ready'
    });
    expect(created.id).toBe(PREFIX + 'go-ready');
    const job = await getJob(PREFIX + 'go-ready', wp.path);
    expect(job.state).toBe('2-ready');
  });

  test('promote backlog job to preparation via /move endpoint', async () => {
    const wp = await firstWatchPath();
    const id = PREFIX + 'promote';
    await fetch(`${BACKEND}/api/tasks`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        id, title: 'Promote me',
        watchPath: wp.path, agent: 'claude', cliType: 'claude',
        fixture: true
      })
    });
    let job = await getJob(id, wp.path);
    expect(job.state).toBe('0-backlog');

    await moveJob(id, wp.path, '1-preparation');
    job = await getJob(id, wp.path);
    expect(job.state).toBe('1-preparation');
  });

  test('UI renders the backlog lane and task-type chip on a card', async ({ page }) => {
    const wp = await firstWatchPath();
    const id = PREFIX + 'visible';
    await fetch(`${BACKEND}/api/tasks`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        id, title: 'Visible bug card',
        watchPath: wp.path, agent: 'claude', cliType: 'claude',
        taskType: 'bug', tags: ['performance'],
        fixture: true
      })
    });

    await page.goto('/?includeFixtures=true');
    // The kanban renders the backlog lane at the leftmost position. We assert
    // by data-state rather than by visible label so a future rename of the
    // lane heading doesn't break the test.
    const lane = page.locator('[data-state="0-backlog"]').first();
    await expect(lane).toBeVisible();

    // The card carries a task-type chip with the bug data attribute.
    const card = page.locator('[data-testid="job-card"]', { hasText: 'Visible bug card' }).first();
    await expect(card).toBeVisible();
    await expect(card.locator('[data-testid="job-task-type"]')).toHaveAttribute('data-task-type', 'bug');

    // The performance tag chip renders on the card.
    const tagChip = card.locator('[data-tag-id="performance"]');
    await expect(tagChip).toBeVisible();
  });

  test('type filter pill narrows the kanban', async ({ page }) => {
    const wp = await firstWatchPath();
    // Seed two jobs of different types so we can prove the filter narrows.
    for (const [slug, taskType] of [['bug-card', 'bug'], ['feature-card', 'feature']] as const) {
      await fetch(`${BACKEND}/api/tasks`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          id: PREFIX + slug, title: `Filter ${slug}`,
          watchPath: wp.path, agent: 'claude', cliType: 'claude',
          taskType, fixture: true
        })
      });
    }

    await page.goto('/?includeFixtures=true');
    // After the header-filter-dropdown refactor, type/tag controls live
    // inside the Filters dropdown; open it before clicking the pill.
    await page.getByTestId('filters-dropdown-trigger').click();
    const bugBtn = page.getByTestId('type-filter-bug');
    await expect(bugBtn).toBeVisible();
    await bugBtn.click();

    // After filtering to Bug, the bug card stays visible and the feature card disappears.
    await expect(page.locator('[data-testid="job-card"]', { hasText: 'Filter bug-card' })).toBeVisible();
    await expect(page.locator('[data-testid="job-card"]', { hasText: 'Filter feature-card' })).toHaveCount(0);

    // The URL hash records the active filter so a copy-paste reproduces the view.
    await expect.poll(() => page.url()).toMatch(/filters=/);
    expect(decodeURIComponent(new URL(page.url()).hash)).toContain('type:bug');

    // Clearing returns the feature card to the board (Clear all sits in the
    // active-filter strip below the header).
    await page.getByTestId('filter-clear-all').click();
    await expect(page.locator('[data-testid="job-card"]', { hasText: 'Filter feature-card' })).toBeVisible();
  });
});
