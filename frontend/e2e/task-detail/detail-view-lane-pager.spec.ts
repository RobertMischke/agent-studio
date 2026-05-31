import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
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
 * Ensure every job in `ids` is rendered as a card on the kanban before the
 * test proceeds. The pager snapshot is captured at the moment of click, so
 * if even one fixture has not landed in `/api/jobs/grouped` yet the
 * iteration starts short and the rest of the test asserts against stale
 * state.
 */
async function waitForFixtureCards(page: Page, ids: ReadonlyArray<string>): Promise<void> {
  for (const id of ids) {
    await expect(page.locator('[data-testid="job-card"]', { hasText: id }).first())
      .toBeVisible({ timeout: 15_000 });
  }
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
      created.push(await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false }));
    }

    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
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

  test('state change from the detail header auto-advances to the next captured slug and removes the moved task from the pager', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`stable-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }
    try {
      await page.goto('/');
      // Wait for every fixture card to render BEFORE the click that captures
      // the pager snapshot - otherwise openDetail can grab a list that's
      // missing the last-created jobs and the rest of the test races.
      await waitForFixtureCards(page, ids);
      // Open the third job, then advance once so we're on the fourth.
      await clickCardAndWaitForDetail(page, ids[2]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const startTotal = Number(totalStr);

      await page.getByTestId('lane-pager-next').click();
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[3])}`), { timeout: 10_000 });

      // Move the current (4th) job out of 2-ready. The user wants to keep
      // triaging the 2-ready lane, so the detail panel must auto-advance to
      // ids[4] - the next captured slug - and the moved task must vanish
      // from the pager (k / N-1, where the same numeric index now points at
      // the next job in the iteration).
      const moveResponse = page.waitForResponse(resp =>
        resp.request().method() === 'POST'
        && resp.url().includes(`/api/tasks/${encodeURIComponent(ids[3])}/move`)
      );
      await page.getByTestId('detail-state-select').selectOption('4-auto-review');
      await moveResponse;

      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[4])}`), { timeout: 10_000 });
      // The new detail view is anchored on ids[4], which is still in 2-ready.
      await expect(page.getByTestId('detail-state-select')).toHaveValue('2-ready', { timeout: 10_000 });
      // Pager count shrunk by one and the position is preserved.
      await expect(count).toHaveText(`${startPos + 1} / ${startTotal - 1}`, { timeout: 5_000 });
    } finally {
      // The fourth job was moved to auto-review; the rest are in 2-ready.
      for (const id of ids) {
        await moveJob(id, wp.path, '7-archive').catch(() => {});
        await deleteJob(id, wp.path).catch(() => {});
      }
    }
  });

  test('delete from the detail menu auto-advances to the next captured slug in the original lane', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`del-adv-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }
    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      await clickCardAndWaitForDetail(page, ids[2]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const startTotal = Number(totalStr);

      // Walk Next once so we're on the fourth slot (ids[3]).
      await page.getByTestId('lane-pager-next').click();
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[3])}`), { timeout: 10_000 });

      // Delete via the detail-header triage overflow menu. The view must
      // land on the next slot (ids[4]) and the pager count must drop by one
      // with the position preserved.
      await page.getByTestId('triage-overflow-btn').click();
      await page.getByTestId('triage-overflow-item-delete').click();
      const confirmDialog = page.getByTestId('confirm-dialog');
      await expect(confirmDialog).toBeVisible({ timeout: 5_000 });
      const deleteResponse = page.waitForResponse(resp =>
        resp.request().method() === 'DELETE'
        && resp.url().includes(`/api/tasks/${encodeURIComponent(ids[3])}`)
      );
      await page.getByTestId('confirm-dialog-confirm').click();
      await deleteResponse;

      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[4])}`), { timeout: 10_000 });
      await expect(count).toHaveText(`${startPos + 1} / ${startTotal - 1}`, { timeout: 5_000 });
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('multiple state changes in a row keep the pager anchored on the original lane', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`multi-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      // Open the first fixture job. The pager captures the 2-ready iteration;
      // total includes our 5 fixtures plus any leftover 2-ready peers.
      await clickCardAndWaitForDetail(page, ids[0]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const startTotal = Number(totalStr);

      // Triage three of the five fixtures in a row via the lane dropdown.
      // The pager must keep the same numeric position while the total drops
      // by one each time, and the panel must land on the next captured slug
      // each time without the user touching prev/next.
      for (let i = 0; i < 3; i++) {
        const movingId = ids[i];
        await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(movingId)}`), { timeout: 10_000 });
        const moveResp = page.waitForResponse(resp =>
          resp.request().method() === 'POST'
          && resp.url().includes(`/api/tasks/${encodeURIComponent(movingId)}/move`)
        );
        await page.getByTestId('detail-state-select').selectOption('4-auto-review');
        await moveResp;
        await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[i + 1])}`), { timeout: 10_000 });
        await expect(count).toHaveText(`${startPos} / ${startTotal - (i + 1)}`, { timeout: 5_000 });
        // The lane the detail header shows must still be 2-ready - the next
        // captured slug is anchored on the original lane, not on the lane the
        // user just moved the previous job to.
        await expect(page.getByTestId('detail-state-select')).toHaveValue('2-ready', { timeout: 10_000 });
      }
    } finally {
      // Three of the fixtures landed in auto-review; the rest in 2-ready.
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
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
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

  test('deep-link with no stored iteration still shows the pager without keyboard nav', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3].map(i => uid(`deep-link-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      // Hit the board first so the fixture cards land in /api/jobs/grouped;
      // then wipe sessionStorage so the next navigation has no stored
      // pager snapshot to restore.
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      await page.evaluate(() => sessionStorage.clear());

      // Navigate directly to the detail URL. Before the fix this scenario
      // showed no pager until the user pressed an arrow key; the snapshot
      // was only captured on board click or pager step.
      await page.goto(`/?job=${encodeURIComponent(ids[1])}&watchPath=${encodeURIComponent(wp.path)}`);
      await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });

      const pager = page.getByTestId('lane-pager');
      await expect(pager).toBeVisible({ timeout: 10_000 });
      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/);
      const [, totalStr] = (await count.textContent())!.trim().split('/').map(s => s.trim());
      expect(Number(totalStr)).toBeGreaterThanOrEqual(ids.length);
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('disables prev at the first slot and next at the last', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3].map(i => uid(`bounds-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
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
