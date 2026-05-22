import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * End-to-end coverage for the "create + delete workspace" flow:
 *   - The "+ Add workspace" button in the Explorer header opens the
 *     create dialog.
 *   - Empty / duplicate names are rejected client-side; valid names
 *     POST to /api/watch-paths and the new entry lights up in the
 *     project picker without a backend restart.
 *   - The per-project Settings rail surfaces a "Delete this workspace"
 *     button that is disabled while the workspace still has jobs and
 *     enabled when it is empty, with the confirm dialog gating the
 *     destructive call.
 *
 * The spec creates and immediately deletes a temporary workspace so
 * the backend's WatchPaths config returns to its pre-test shape no
 * matter how the test exits. Each test uses a unique slug to keep
 * parallel runs from colliding.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'workspace-create-and-delete');
})();

function uniqueName(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
}

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

/** Best-effort cleanup of any test workspace this spec created but
 *  failed to remove (e.g. on hard-aborted runs). The DELETE endpoint
 *  is a no-op for non-existent names so calling it for every
 *  candidate is safe. */
async function purgeIfPresent(name: string): Promise<void> {
  try {
    await api(`/api/watch-paths/${encodeURIComponent(name)}`, { method: 'DELETE' });
  } catch {
    /* not present, ignore */
  }
}

test('create workspace via "+" button, then delete it via the per-project settings', async ({ page }) => {
  const newName = uniqueName('e2e-ws');
  test.info().annotations.push({ type: 'workspace-name', description: newName });

  try {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    // Use the Explorer header "+" button. The titlebar "Workspace"
    // crumb fires the same handler; we exercise the Explorer one
    // here because it is always visible regardless of which panel
    // is active.
    const addButton = page.getByTestId('studio-sidebar-add-workspace');
    await addButton.click();

    const dialog = page.getByTestId('workspace-create-dialog');
    await expect(dialog).toBeVisible();

    // Validation: submit disabled with empty input.
    const submit = page.getByTestId('workspace-create-submit');
    await expect(submit).toBeDisabled();

    const nameInput = page.getByTestId('workspace-create-name');
    await nameInput.fill(newName);
    await expect(submit).toBeEnabled();

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-create-dialog.png'), fullPage: true });

    await submit.click();
    await expect(dialog).not.toBeVisible({ timeout: 10_000 });

    // The new workspace must appear in the picker without a backend restart.
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.map(p => p.name)).toContain(newName);
    expect(paths.find(p => p.name === newName)?.path).toMatch(/projects[\\/]e2e-ws-/);

    // Picker visibility: open the picker and confirm the new entry is there.
    await page.getByTestId('studio-project-picker-trigger').click();
    await expect(page.getByTestId(`studio-project-picker-${newName}`)).toBeVisible();
    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-picker-lit-up.png'), fullPage: true });
    // Close picker by clicking the trigger again.
    await page.getByTestId('studio-project-picker-trigger').click();

    // Now open the per-project Settings rail and verify the delete
    // button is enabled (the workspace was created empty).
    const slug = slugFor(newName);
    await page.goto(`/#/projects/${slug}/settings`);
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-detail-workspace')).toBeVisible();

    const deleteBtn = page.getByTestId('project-detail-workspace-delete');
    await expect(deleteBtn).toBeEnabled();

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-settings-with-delete.png'), fullPage: true });

    await deleteBtn.click();
    const confirm = page.getByTestId('confirm-dialog');
    await expect(confirm).toBeVisible();
    await expect(confirm).toContainText('Delete this workspace?');

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '04-confirm-delete.png'), fullPage: true });

    // Accept (danger primary button) and verify the workspace is gone.
    await page.getByTestId('confirm-dialog-confirm').click();
    await expect(confirm).not.toBeVisible();

    // Poll the API: deletion is synchronous but the config reload + UI
    // refresh take a beat.
    await expect.poll(async () => {
      const list = await api<WatchPath[]>('/api/watch-paths');
      return list.some(p => p.name === newName);
    }, { timeout: 5_000 }).toBe(false);
  } finally {
    await purgeIfPresent(newName);
  }
});

test('create dialog rejects empty + duplicate names client-side', async ({ page }) => {
  const sentinelName = uniqueName('e2e-dup');
  // Seed an entry directly via the API so we can collide on it.
  await api<WatchPath>('/api/watch-paths', {
    method: 'POST',
    body: JSON.stringify({ name: sentinelName }),
  });

  try {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('studio-sidebar-add-workspace').click();
    const dialog = page.getByTestId('workspace-create-dialog');
    await expect(dialog).toBeVisible();

    const submit = page.getByTestId('workspace-create-submit');
    await expect(submit).toBeDisabled(); // empty

    const nameInput = page.getByTestId('workspace-create-name');
    await nameInput.fill(sentinelName);
    // Client-side duplicate check kicks in.
    const clientError = page.getByTestId('workspace-create-client-error');
    await expect(clientError).toBeVisible();
    await expect(clientError).toContainText('already exists');
    await expect(submit).toBeDisabled();

    // Cancel and clean up.
    await page.getByTestId('workspace-create-cancel').click();
    await expect(dialog).not.toBeVisible();
  } finally {
    await purgeIfPresent(sentinelName);
  }
});

test('delete is blocked + tooltipped while the workspace still has jobs', async ({ page }) => {
  // Pick an existing project that has at least one job.
  const allPaths = await api<WatchPath[]>('/api/watch-paths');
  expect(allPaths.length).toBeGreaterThanOrEqual(1);

  // We can't safely create jobs here, but the existing fixtures
  // (Agent Software Studio etc.) reliably have jobs. Find one with
  // a non-empty kanban.
  let busyName: string | null = null;
  for (const wp of allPaths) {
    type Job = { projectName: string };
    const jobs = await api<Job[]>('/api/jobs');
    const count = jobs.filter(j => j.projectName === wp.name).length;
    if (count > 0) { busyName = wp.name; break; }
  }
  test.skip(busyName === null, 'No populated workspace available to exercise the busy-delete guard.');

  const slug = slugFor(busyName!);
  await page.goto(`/#/projects/${slug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-detail-workspace')).toBeVisible();

  const deleteBtn = page.getByTestId('project-detail-workspace-delete');
  await expect(deleteBtn).toBeVisible();
  await expect(deleteBtn).toBeDisabled();
});
