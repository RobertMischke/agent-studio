/**
 * Tag manager dialog (slice E follow-up to backlog-lane-task-types-and-tags).
 *
 * Drives the dev-tools menu → Tag manager flow end-to-end against dev's
 * backend via the `dev-backend` fixture. Asserts the acceptance bullets:
 *   - the DevTools menu has a `Tag manager` entry that opens the dialog
 *   - the dialog lists every registered tag with its colour, label, id
 *   - the Add form creates a tag visible immediately in the kanban filter
 *     dropdown (no reload), and inline error surfaces 409 on duplicate id
 *   - the Delete flow soft-removes the tag from the registry; immediate
 *     re-list confirms it's gone (filter dropdown no longer shows it)
 *   - the Edit flow swaps label/colour/description for the same id and
 *     the result persists across a reload
 *
 * Re-seed-on-boot is covered by `BacklogLaneAndTagsTests` because the
 * Playwright fixture intentionally owns backend start/stop. This spec asserts
 * the user-facing warning that default seed tags stay deleted after explicit
 * removal.
 *
 * Cleanup deletes any custom tags created by the test (best-effort).
 */
import { test, expect } from '../fixtures/dev-backend';

const CLIENT_ID = 'local-default';
const TEST_TAG_ID = 'e2e-tagmgr-new';
const TEST_TAG_LABEL = 'E2E tag manager';
const TEST_JOB_ID = 'e2e-tagmgr-ghost-card';

async function apiRequest(baseUrl: string, path: string, init: RequestInit = {}): Promise<Response> {
  return fetch(`${baseUrl}${path}`, {
    headers: {
      'content-type': 'application/json',
      'x-client-id': CLIENT_ID,
      ...(init.headers ?? {}),
    },
    ...init,
  });
}

async function deleteTagViaApi(baseUrl: string, id: string): Promise<void> {
  await apiRequest(baseUrl, `/api/tags/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

async function firstWatchPath(baseUrl: string): Promise<{ path: string }> {
  const res = await apiRequest(baseUrl, '/api/watch-paths');
  const paths = (await res.json()) as Array<{ path: string }>;
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJobViaApi(baseUrl: string, id: string, watchPath: string): Promise<void> {
  await apiRequest(
    baseUrl,
    `/api/jobs/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' },
  );
}

test.describe('Tag manager dialog', () => {
  test.beforeEach(async ({ devBackend, page }) => {
    const wp = await firstWatchPath(devBackend.baseUrl);
    await deleteTagViaApi(devBackend.baseUrl, TEST_TAG_ID);
    await deleteJobViaApi(devBackend.baseUrl, TEST_JOB_ID, wp.path).catch(() => {});
    // The devtools menu is currently rendered only in the legacy header
    // (`@else` branch of app.html). The vsCodeLayout flag is default-on,
    // so flip it off for the test session before the first paint.
    await page.addInitScript(() => {
      try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* noop */ }
    });
  });

  test.afterAll(async ({ devBackend }) => {
    const wp = await firstWatchPath(devBackend.baseUrl);
    await deleteTagViaApi(devBackend.baseUrl, TEST_TAG_ID);
    await deleteJobViaApi(devBackend.baseUrl, TEST_JOB_ID, wp.path).catch(() => {});
  });

  test('opens from dev-tools menu and lists registered tags', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('devtools-menu-trigger').click();
    const menuItem = page.getByTestId('devtools-menu-item-tag-manager');
    await expect(menuItem).toBeVisible();
    await menuItem.click();

    const dialog = page.getByTestId('tag-manager-dialog');
    await expect(dialog).toBeVisible();
    const list = page.getByTestId('tag-manager-list');
    await expect(list).toBeVisible();
    // The seven taxonomy seeds plus orchestrator-moved + outcome-silent-finish
    // must all be visible; assert a representative subset rather than the
    // full count so this stays robust when new seeds are added.
    await expect(page.getByTestId('tag-manager-row-ui-ux')).toBeVisible();
    await expect(page.getByTestId('tag-manager-row-architecture')).toBeVisible();
    await expect(page.getByTestId('tag-manager-row-quality')).toBeVisible();

    await page.screenshot({
      path: 'test-results/tag-manager-dialog-open.png',
      fullPage: false,
    });
  });

  test('Add tag → visible immediately in filter dropdown without reload', async ({ page, devBackend }) => {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('devtools-menu-trigger').click();
    await page.getByTestId('devtools-menu-item-tag-manager').click();
    await expect(page.getByTestId('tag-manager-dialog')).toBeVisible();

    await page.getByTestId('tag-manager-add-toggle').click();
    await page.getByTestId('tag-manager-add-label').fill(TEST_TAG_LABEL);
    await page.getByTestId('tag-manager-add-id').fill(TEST_TAG_ID);
    await page.getByTestId('tag-manager-add-desc').fill('Created from the tag-manager-dialog spec.');
    await page.getByTestId('tag-manager-add-submit').click();

    // Row should appear; backend should confirm.
    await expect(page.getByTestId(`tag-manager-row-${TEST_TAG_ID}`)).toBeVisible();
    const apiRes = await apiRequest(devBackend.baseUrl, '/api/tags');
    const tags = (await apiRes.json()) as Array<{ id: string; label: string }>;
    expect(tags.some((t) => t.id === TEST_TAG_ID)).toBe(true);

    await page.getByTestId('tag-manager-close').click();

    // Confirm the new tag is exposed via the kanban filter without a reload.
    await page.getByTestId('filters-dropdown-trigger').click();
    await expect(page.getByTestId('filters-dropdown-panel')).toBeVisible();
    await expect(page.getByTestId(`tag-filter-row-${TEST_TAG_ID}`)).toBeVisible();
  });

  test('duplicate id → inline 409 error', async ({ page, devBackend }) => {
    // Pre-create the conflict target via API.
    const create = await apiRequest(devBackend.baseUrl, '/api/tags', {
      method: 'POST',
      body: JSON.stringify({ id: TEST_TAG_ID, label: TEST_TAG_LABEL, color: '#aaaaaa', description: '' }),
    });
    expect(create.ok).toBe(true);

    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('devtools-menu-trigger').click();
    await page.getByTestId('devtools-menu-item-tag-manager').click();

    await page.getByTestId('tag-manager-add-toggle').click();
    await page.getByTestId('tag-manager-add-label').fill('Conflicting label');
    await page.getByTestId('tag-manager-add-id').fill(TEST_TAG_ID);
    await page.getByTestId('tag-manager-add-submit').click();

    const err = page.getByTestId('tag-manager-add-error');
    await expect(err).toBeVisible();
    await expect(err).toContainText(TEST_TAG_ID);
  });

  test('Delete tag → registry drops it and tagged cards render a ghost chip', async ({ page, devBackend }) => {
    // Seed the tag we will delete.
    await apiRequest(devBackend.baseUrl, '/api/tags', {
      method: 'POST',
      body: JSON.stringify({ id: TEST_TAG_ID, label: TEST_TAG_LABEL, color: '#aaaaaa', description: '' }),
    });
    const wp = await firstWatchPath(devBackend.baseUrl);
    await apiRequest(devBackend.baseUrl, '/api/jobs', {
      method: 'POST',
      body: JSON.stringify({
        id: TEST_JOB_ID,
        title: 'Tag manager ghost card',
        watchPath: wp.path,
        agent: 'claude',
        cliType: 'claude',
        taskType: 'bug',
        tags: [TEST_TAG_ID],
        fixture: true,
      }),
    });

    await page.goto('/?includeFixtures=true');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('devtools-menu-trigger').click();
    await page.getByTestId('devtools-menu-item-tag-manager').click();
    await expect(page.getByTestId(`tag-manager-row-${TEST_TAG_ID}`)).toBeVisible();

    await page.getByTestId(`tag-manager-delete-${TEST_TAG_ID}`).click();
    await expect(page.getByTestId('confirm-dialog')).toContainText('Default seed tags stay deleted too');
    await page.getByTestId('confirm-dialog-confirm').click();

    await expect(page.getByTestId(`tag-manager-row-${TEST_TAG_ID}`)).toBeHidden();

    // API should agree the tag is gone.
    const apiRes = await apiRequest(devBackend.baseUrl, '/api/tags');
    const tags = (await apiRes.json()) as Array<{ id: string }>;
    expect(tags.some((t) => t.id === TEST_TAG_ID)).toBe(false);

    await page.getByTestId('tag-manager-close').click();
    const card = page.locator('[data-testid="job-card"]', { hasText: 'Tag manager ghost card' }).first();
    await expect(card).toBeVisible();
    const ghost = card.locator(`[data-tag-id="${TEST_TAG_ID}"]`);
    await expect(ghost).toBeVisible();
    await expect(ghost).toContainText(TEST_TAG_ID);
    await expect(ghost).toHaveClass(/task-card__tag-chip--ghost/);

    await page.getByTestId('filters-dropdown-trigger').click();
    await expect(page.getByTestId(`tag-filter-row-${TEST_TAG_ID}`)).toHaveCount(0);
  });

  test('Edit tag → new label persists across reload', async ({ page, devBackend }) => {
    await apiRequest(devBackend.baseUrl, '/api/tags', {
      method: 'POST',
      body: JSON.stringify({ id: TEST_TAG_ID, label: 'old label', color: '#aaaaaa', description: 'before' }),
    });

    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('devtools-menu-trigger').click();
    await page.getByTestId('devtools-menu-item-tag-manager').click();
    await expect(page.getByTestId(`tag-manager-row-${TEST_TAG_ID}`)).toBeVisible();

    await page.getByTestId(`tag-manager-edit-${TEST_TAG_ID}`).click();
    await page.getByTestId('tag-manager-edit-label').fill('new label');
    await page.getByTestId('tag-manager-edit-desc').fill('after');
    await page.getByTestId('tag-manager-edit-save').click();

    // Row label should reflect the new value immediately.
    const row = page.getByTestId(`tag-manager-row-${TEST_TAG_ID}`);
    await expect(row).toContainText('new label');

    // Verify backend persisted the change.
    const apiRes = await apiRequest(devBackend.baseUrl, '/api/tags');
    const tags = (await apiRes.json()) as Array<{ id: string; label: string; description: string }>;
    const entry = tags.find((t) => t.id === TEST_TAG_ID);
    expect(entry?.label).toBe('new label');
    expect(entry?.description).toBe('after');

    // Reload and re-open the dialog: the change must still be present.
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('devtools-menu-trigger').click();
    await page.getByTestId('devtools-menu-item-tag-manager').click();
    await expect(page.getByTestId(`tag-manager-row-${TEST_TAG_ID}`)).toContainText('new label');
  });
});
