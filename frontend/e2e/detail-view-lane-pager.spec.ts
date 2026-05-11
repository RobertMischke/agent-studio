import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, moveJob } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

function uid(suffix: string) {
  return `e2e-pager-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function clickCardAndWaitForDetail(page: Page, jobId: string): Promise<void> {
  await page.locator('[data-testid="job-card"]', { hasText: jobId }).first().click();
  await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(jobId)}`), { timeout: 10_000 });
  await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });
}

/**
 * Detail-header lane pager. The Prev / N of M / Next controls iterate the
 * snapshot captured when the user clicked into the detail view, NOT the
 * live lane: changing a job's status mid-iteration must keep the pager's
 * place in the original lane. Reloads restore the iteration from
 * sessionStorage.
 *
 * Plain-text `title` tooltips (no rich HTML) per the project tooltip rule.
 */
test.describe('Detail view - lane pager', () => {
  test('captures lane snapshot on entry and walks with prev/next', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`walk-${i}`));
    const created: { id: string }[] = [];
    for (const id of ids) {
      created.push(await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready' }));
    }

    try {
      await page.goto('/');
      // Land on the third created job. Created in order so it's the third
      // of our fixture set; we click by its unique title.
      await clickCardAndWaitForDetail(page, ids[2]);

      const pager = page.getByTestId('lane-pager');
      await expect(pager).toBeVisible({ timeout: 10_000 });

      // The lane has at least our 5 fixtures, so position is "3 / N" with N >= 5.
      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/);
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const total = Number(totalStr);
      expect(total).toBeGreaterThanOrEqual(5);
      const startPos = Number(posStr);

      // Tooltip is plain text on the title attribute - no rich HTML widget.
      const titleAttr = await pager.getAttribute('title');
      expect(titleAttr).toMatch(/Iterating jobs.*Showing job \d+ of \d+/);
      // Sanity-check: no nested tooltip-renderer element on or under the pager.
      const customTooltipMarkers = await pager.locator('[data-tooltip-html],[data-tippy-content]').count();
      expect(customTooltipMarkers).toBe(0);

      // Next advances to the fourth job in the captured iteration.
      await page.getByTestId('lane-pager-next').click();
      await expect(count).toHaveText(`${startPos + 1} / ${total}`, { timeout: 5_000 });
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[3])}`), { timeout: 10_000 });

      // Prev rewinds.
      await page.getByTestId('lane-pager-prev').click();
      await expect(count).toHaveText(`${startPos} / ${total}`, { timeout: 5_000 });
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[2])}`), { timeout: 10_000 });
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('status change preserves snapshot position - Next walks to the next captured slug, not the live lane', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`stable-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready' });
    }

    try {
      await page.goto('/');
      // Open the third job, then advance once so we're on the fourth.
      await clickCardAndWaitForDetail(page, ids[2]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);

      await page.getByTestId('lane-pager-next').click();
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[3])}`), { timeout: 10_000 });

      // Now move the current job (the fourth) to 4-auto-review. The pager
      // must NOT lose its place - the captured iteration is for "2-ready",
      // independent of the moved job's current state.
      await page.getByTestId('detail-state-select').selectOption('4-auto-review');
      // Wait for the select to reflect the new state - the move has landed.
      await expect(page.getByTestId('detail-state-select')).toHaveValue('4-auto-review', { timeout: 10_000 });

      // The pager is still on the same captured slot (the moved job).
      await expect(count).toHaveText(`${startPos + 1} / ${initial.split('/')[1].trim()}`, { timeout: 5_000 });

      // Clicking Next now must advance through the ORIGINAL snapshot.
      await page.getByTestId('lane-pager-next').click();
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[4])}`), { timeout: 10_000 });
    } finally {
      // The fourth job was moved to auto-review; the rest are in 2-ready.
      for (const id of ids) {
        await moveJob(id, wp.path, '7-archive').catch(() => {});
        await deleteJob(id, wp.path).catch(() => {});
      }
    }
  });

  test('reload restores the iteration state', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4].map(i => uid(`reload-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready' });
    }

    try {
      await page.goto('/');
      await clickCardAndWaitForDetail(page, ids[1]);

      const before = (await page.getByTestId('lane-pager-count').textContent())!.trim();

      await page.reload();
      await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });
      const pager = page.getByTestId('lane-pager');
      await expect(pager).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('lane-pager-count')).toHaveText(before, { timeout: 5_000 });
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('disables prev at the first slot and next at the last', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3].map(i => uid(`bounds-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready' });
    }

    try {
      await page.goto('/');
      await clickCardAndWaitForDetail(page, ids[0]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const total = Number(totalStr);

      if (startPos === 1) {
        await expect(page.getByTestId('lane-pager-prev')).toBeDisabled();
      }
      if (startPos === total) {
        await expect(page.getByTestId('lane-pager-next')).toBeDisabled();
      }
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });
});
