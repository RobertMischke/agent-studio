import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJobApi(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

async function cleanupTestJobs(watchPath: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith('e2e-del-'));
  await Promise.all(stale.map(j => deleteJobApi(j.id, j.watchPath).catch(() => {})));
}

function uid() {
  return `e2e-del-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/** Find the kanban card belonging to a specific job id. */
function cardLocator(page: Page, jobId: string) {
  return page.locator(`[data-testid="job-card"]`, { hasText: jobId });
}

test.describe('Delete task', () => {
  test('hover-revealed delete button on a kanban card prompts and removes the job', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanupTestJobs(watchPath);

    const id = uid();
    const job = await createJob({
      id,
      title: `e2e-del-card-${id}`,
      watchPath,
      targetState: '1-preparation',
      promptMarkdown: 'Fixture for delete-from-card test.'
    });

    page.on('dialog', d => d.accept());

    await page.goto('/');
    const card = cardLocator(page, job.id);
    await expect(card).toBeVisible({ timeout: 10_000 });

    await card.hover();
    const trash = card.getByTestId('job-card-delete');
    await expect(trash).toBeVisible();
    await trash.click();

    // Card should disappear after the backend confirms + refresh tick.
    await expect(card).toHaveCount(0, { timeout: 10_000 });

    const after = await listJobs();
    expect(after.find(j => j.id === job.id)).toBeUndefined();
  });

  test('detail context menu Delete item prompts, deletes, and closes the detail view', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanupTestJobs(watchPath);

    const id = uid();
    const job = await createJob({
      id,
      title: `e2e-del-detail-${id}`,
      watchPath,
      targetState: '1-preparation',
      promptMarkdown: 'Fixture for delete-from-detail test.'
    });

    page.on('dialog', d => d.accept());

    await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

    const menuBtn = page.getByTestId('detail-menu-btn');
    await expect(menuBtn).toBeVisible({ timeout: 10_000 });
    await menuBtn.click();

    const deleteItem = page.getByTestId('detail-menu-delete');
    await expect(deleteItem).toBeVisible();
    await deleteItem.click();

    // Detail view closes; the kanban dashboard becomes visible again.
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    const after = await listJobs();
    expect(after.find(j => j.id === job.id)).toBeUndefined();
  });

  test('cancelling the confirm dialog leaves the task in place', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanupTestJobs(watchPath);

    const id = uid();
    const job = await createJob({
      id,
      title: `e2e-del-cancel-${id}`,
      watchPath,
      targetState: '1-preparation',
      promptMarkdown: 'Fixture for cancel-delete test.'
    });

    page.on('dialog', d => d.dismiss());

    await page.goto('/');
    const card = cardLocator(page, job.id);
    await expect(card).toBeVisible({ timeout: 10_000 });
    await card.hover();
    await card.getByTestId('job-card-delete').click();

    // Wait briefly to make sure no delete went through.
    await page.waitForTimeout(500);
    const after = await listJobs();
    expect(after.find(j => j.id === job.id)).toBeDefined();

    // Final cleanup so the spec leaves no fixture behind.
    await deleteJobApi(job.id, watchPath);
  });
});
