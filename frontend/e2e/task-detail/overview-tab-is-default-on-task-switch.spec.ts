import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, waitForJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  }).catch(() => { /* best-effort cleanup */ });
}

function uid(suffix: string) {
  return `e2e-overview-default-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function openTaskDirectly(page: Page, jobId: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  // detail-panes is always rendered when the panel mounts; the lane
  // dropdown can hide on narrow viewports or for some states, so it is
  // not a reliable mount-check.
  await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 10_000 });
  await dismissUpdateBanners(page);
}

/** Best-effort: close the "Update failed" / "Failed pickup" overlays that
 *  the dev backend often shows on first boot. They float over the detail
 *  header and can hide the lane pager + state dropdown from the click. */
async function dismissUpdateBanners(page: Page): Promise<void> {
  for (const btn of [
    page.getByRole('button', { name: /^Dismiss$/ }).first(),
    page.locator('[aria-label="Dismiss"]').first(),
  ]) {
    if (await btn.count()) {
      try { await btn.click({ timeout: 1_000 }); } catch { /* best-effort */ }
    }
  }
}

/**
 * Operator expectation (polish-overview-tab-is-default-when-opening-or-switching-task):
 * the left-pane Overview tab is the default whenever a task is opened or the
 * active task changes. Within a single task, tab clicks persist; switching to
 * a different task snaps back to Overview so the operator always sees the
 * task title / status / config first.
 *
 * Persistence across page reloads is intentionally out of scope; the active
 * tab lives only in-memory.
 */
test.describe('Overview tab is the default on task open + switch', () => {
  test('opening a task lands on Overview; clicking Files persists within the task', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('open');
    await createJob({ id, title: id, watchPath: wp.path, targetState: '1-preparation' });

    try {
      // Wait for the scanner to surface the new job before the deep-link
      // navigation — without this the URL restore races the JobIndexCache
      // and the welcome screen wins.
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      const overviewTab = page.getByTestId('prompt-tab-overview');
      const filesTab = page.getByTestId('prompt-tab-description');
      await expect(overviewTab).toHaveClass(/pane-tab--active/);
      await expect(filesTab).not.toHaveClass(/pane-tab--active/);

      await filesTab.click();
      await expect(filesTab).toHaveClass(/pane-tab--active/);
      await expect(overviewTab).not.toHaveClass(/pane-tab--active/);
    } finally {
      await deleteJob(id, wp.path);
    }
  });

  test('switching to a different task snaps back to Overview even after the previous task left Files active', async ({ page }, testInfo) => {
    const wp = await pickWatchPath();
    const idA = uid('switch-a');
    const idB = uid('switch-b');
    await createJob({ id: idA, title: idA, watchPath: wp.path, targetState: '1-preparation' });
    await createJob({ id: idB, title: idB, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      await waitForJob(idA, wp.path, () => true, { timeoutMs: 15_000 });
      await waitForJob(idB, wp.path, () => true, { timeoutMs: 15_000 });

      // Open Task A and switch to Files.
      await openTaskDirectly(page, idA, wp.path);
      const overviewTab = page.getByTestId('prompt-tab-overview');
      const filesTab = page.getByTestId('prompt-tab-description');
      await expect(overviewTab).toHaveClass(/pane-tab--active/);
      await filesTab.click();
      await expect(filesTab).toHaveClass(/pane-tab--active/);
      await testInfo.attach('task-a-on-files.png', {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      await page.screenshot({
        path: 'test-results/overview-default-on-switch-task-a-files.png',
        fullPage: false,
      });

      // Switch to Task B. Deep-link with the new id; the active-tab
      // reset must fire so Task B lands on Overview regardless of the
      // selection Task A was left in.
      await openTaskDirectly(page, idB, wp.path);
      await expect(overviewTab).toHaveClass(/pane-tab--active/, { timeout: 5_000 });
      await expect(filesTab).not.toHaveClass(/pane-tab--active/);
      await testInfo.attach('task-b-on-overview.png', {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      await page.screenshot({
        path: 'test-results/overview-default-on-switch-task-b-overview.png',
        fullPage: false,
      });

      // Click Files in Task B, then walk back to Task A. Task A also
      // lands on Overview again — there is no per-task memory.
      await filesTab.click();
      await expect(filesTab).toHaveClass(/pane-tab--active/);

      await openTaskDirectly(page, idA, wp.path);
      await expect(overviewTab).toHaveClass(/pane-tab--active/, { timeout: 5_000 });
      await expect(filesTab).not.toHaveClass(/pane-tab--active/);
    } finally {
      await deleteJob(idA, wp.path);
      await deleteJob(idB, wp.path);
    }
  });

  test('within the same task, clicks across Files / Evidence / Code Review persist while Overview stays inactive', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('within');
    await createJob({ id, title: id, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      const overviewTab = page.getByTestId('prompt-tab-overview');
      const filesTab = page.getByTestId('prompt-tab-description');
      const evidenceTab = page.getByTestId('prompt-tab-evidence');
      const codeReviewTab = page.getByTestId('prompt-tab-code-review');

      await expect(overviewTab).toHaveClass(/pane-tab--active/);

      await filesTab.click();
      await expect(filesTab).toHaveClass(/pane-tab--active/);

      await evidenceTab.click();
      await expect(evidenceTab).toHaveClass(/pane-tab--active/);
      await expect(filesTab).not.toHaveClass(/pane-tab--active/);

      await codeReviewTab.click();
      await expect(codeReviewTab).toHaveClass(/pane-tab--active/);
      await expect(evidenceTab).not.toHaveClass(/pane-tab--active/);

      // Overview must not be auto-resurrected by a same-task refresh:
      // confirm the operator's last manual pick still wins after a moment.
      await page.waitForTimeout(250);
      await expect(codeReviewTab).toHaveClass(/pane-tab--active/);
    } finally {
      await deleteJob(id, wp.path);
    }
  });
});
