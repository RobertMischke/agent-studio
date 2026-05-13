import { test, expect } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, listJobs, moveJob } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

function uid() {
  return `e2e-arch-load-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function dismissErrorOverlay(page: import('@playwright/test').Page) {
  const overlay = page.locator('.overlay--error');
  if (await overlay.isVisible({ timeout: 500 }).catch(() => false)) {
    await overlay.click({ force: true }).catch(() => {});
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

/**
 * Regression coverage for the "Archive all" loading indicator:
 *
 *   - While the bulk archive POSTs are in flight, the button must show
 *     the in-flight signal (data-archiving="true", aria-busy="true",
 *     visible spinner, "Archiving…" label) and be disabled so a panicked
 *     double-click can't fire a second batch.
 *   - Once the POSTs resolve, the button must return to its idle state
 *     (no data-archiving attr, "Archive all" label, enabled).
 *
 * The move requests are intentionally delayed via `page.route` so the
 * loading state is observable from the browser. Without the delay the
 * archive completes faster than Playwright's polling cadence and the
 * assertion would race with the response.
 */
test.describe('Archive-all loading indicator', () => {
  test('shows spinner + disabled while archive is in flight, then resets', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    const idA = uid();
    const idB = uid();
    const jobA = await createJob({ id: idA, title: `e2e-arch-load-${idA}`, watchPath, targetState: '2-ready' });
    const jobB = await createJob({ id: idB, title: `e2e-arch-load-${idB}`, watchPath, targetState: '2-ready' });
    // Walk both jobs into 5-completed / 6-completed via the API. The
    // canArchiveAll() guard accepts either lane name, but the move pipeline
    // here is ADR-0025: ready -> auto-review -> human-review -> completed.
    try {
      for (const id of [jobA.id, jobB.id]) {
        await moveJob(id, watchPath, '3-progress');
        await moveJob(id, watchPath, '4-auto-review');
        await moveJob(id, watchPath, '5-human-review');
        await moveJob(id, watchPath, '6-completed');
      }
    } catch {
      // Tolerate transitional schemas (5-completed / 4-review). Fall back
      // to a single ready -> completed jump; the Completed column accepts it.
      for (const id of [jobA.id, jobB.id]) {
        await moveJob(id, watchPath, '5-completed').catch(() => {});
      }
    }

    let movePostCount = 0;
    let resolveGate: (() => void) | null = null;
    const gate = new Promise<void>(r => { resolveGate = r; });

    // Hold the move POSTs until the loading-state assertions have run.
    // `page.route` intercepts the browser-side fetch and lets us pause
    // before forwarding it.
    await page.route('**/api/jobs/*/move*', async (route, request) => {
      if (request.method() !== 'POST') return route.continue();
      movePostCount += 1;
      await gate;
      return route.continue();
    });

    try {
      await page.goto('/');
      await dismissErrorOverlay(page);

      const btn = page.getByTestId('archive-all-btn');
      await expect(btn).toBeVisible({ timeout: 10_000 });
      // Idle baseline.
      await expect(btn).not.toHaveAttribute('data-archiving', /.+/);
      await expect(btn).toBeEnabled();

      await btn.click();

      // In-flight: disabled, archiving attr set, spinner visible, label switched.
      await expect(btn).toHaveAttribute('data-archiving', 'true', { timeout: 2_000 });
      await expect(btn).toHaveAttribute('aria-busy', 'true');
      await expect(btn).toBeDisabled();
      await expect(btn).toContainText(/Archiving/i);
      await expect(btn.locator('.column__archive-all__spinner')).toBeVisible();

      // Double-click during the loading state must not fan out additional
      // POSTs: the service's `archiving` guard suppresses re-entry, and the
      // disabled button blocks the click at the DOM layer.
      const before = movePostCount;
      await btn.click({ force: true }).catch(() => {});
      await page.waitForTimeout(150);
      expect(movePostCount).toBe(before);

      // Release the gate and let the POSTs land.
      resolveGate?.();

      // Idle again. Use a slightly longer timeout because the button only
      // resets after both moves resolve + the board refresh repaints.
      await expect(btn).not.toHaveAttribute('data-archiving', /.+/, { timeout: 10_000 });
      await expect(btn).toBeEnabled();
      await expect(btn).toContainText(/Archive all/i);
    } finally {
      await page.unroute('**/api/jobs/*/move*').catch(() => {});
      // resolveGate may not have been called if an assertion failed early;
      // release it so the route handler doesn't keep the page hung on cleanup.
      resolveGate?.();
      const jobs = await listJobs();
      for (const id of [jobA.id, jobB.id]) {
        const live = jobs.find(j => j.id === id);
        if (live) await deleteJob(live.id, live.watchPath).catch(() => {});
      }
    }
  });
});
