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
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  }).catch(() => { /* best-effort cleanup */ });
}

function uid(suffix: string) {
  return `e2e-overview-title-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function openTaskDirectly(page: Page, jobId: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 10_000 });
}

/**
 * Operator expectation (polish-overview-tab-prominent-task-title-at-top):
 * the Overview tab carries a Hero-typography task title as its first
 * element, with a sub-line of key + lane + project, plus inline-edit
 * (click affordance → input → Enter persists via PUT, Esc cancels).
 * The title must stay legible across themes and not regress the
 * existing Status / Agent column blocks.
 */
test.describe('Overview tab — prominent task title at top', () => {
  test('title is the first visible element on Overview with a sub-line and lane pill', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('first-element');
    const title = `Prominent title test ${id}`;
    await createJob({ id, title, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      const titleBlock = page.getByTestId('overview-title-block');
      await expect(titleBlock).toBeVisible({ timeout: 10_000 });

      const titleEl = page.getByTestId('overview-title');
      await expect(titleEl).toHaveText(new RegExp(title));

      const overviewTab = page.getByTestId('overview-tab');
      const blockBox = await titleBlock.boundingBox();
      const statusBox = await page.getByTestId('overview-status').boundingBox();
      expect(blockBox).not.toBeNull();
      expect(statusBox).not.toBeNull();
      if (blockBox && statusBox) {
        // Title block sits above the Status column block.
        expect(blockBox.y).toBeLessThan(statusBox.y);
      }

      await expect(page.getByTestId('overview-title-subline')).toBeVisible();
      await expect(page.getByTestId('overview-title-lane')).toBeVisible();
      await expect(page.getByTestId('overview-title-project')).toContainText(wp.name);

      // The Status and Agent column blocks are still rendered below.
      await expect(overviewTab.getByTestId('overview-status')).toBeVisible();
      await expect(overviewTab.getByTestId('overview-agent')).toBeVisible();
    } finally {
      await deleteJob(id, wp.path);
    }
  });

  test('clicking the title opens the inline editor; Enter persists via PUT /title', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('inline-edit');
    const title = `Editable title ${id}`;
    const renamed = `Renamed title ${id}`;
    await createJob({ id, title, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      // Hover the title so the edit affordance becomes pointer-reachable,
      // then click the affordance to enter edit mode.
      const titleEl = page.getByTestId('overview-title');
      await expect(titleEl).toBeVisible();
      await titleEl.hover();
      await page.getByTestId('overview-title-edit-affordance').click();

      const input = page.getByTestId('overview-title-input');
      await expect(input).toBeVisible();
      await expect(input).toBeFocused();

      // Watch for the PUT roundtrip to confirm the API call fires.
      const putResponse = page.waitForResponse(
        resp => /\/api\/tasks\/.+\/title/.test(resp.url()) && resp.request().method() === 'PUT',
        { timeout: 10_000 },
      );

      await input.fill(renamed);
      await input.press('Enter');

      const resp = await putResponse;
      expect(resp.ok()).toBeTruthy();
      // Surface the actual body the frontend sent so we can tell whether the
      // value the user typed made it into the PUT.
      const sentBody = resp.request().postData() ?? '';
      expect(sentBody).toContain(renamed);

      // Optimistic display: the new title shows up immediately and the
      // editor goes away.
      await expect(titleEl).toHaveText(new RegExp(renamed));
      await expect(input).not.toBeVisible();

      // Verify the change was actually persisted on the backend. The job
      // index cache may settle just after the PUT response returns, so
      // poll the GET briefly before giving up.
      let persisted: string | null = null;
      for (let i = 0; i < 10; i++) {
        const fresh = await api<{ info: { title: string } }>(
          `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(wp.path)}`,
        );
        persisted = fresh.info.title;
        if (persisted === renamed) break;
        await page.waitForTimeout(200);
      }
      expect(persisted).toBe(renamed);
    } finally {
      await deleteJob(id, wp.path);
    }
  });

  test('Escape cancels the inline edit without firing a PUT', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('escape-cancel');
    const title = `Original title ${id}`;
    await createJob({ id, title, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      const titleEl = page.getByTestId('overview-title');
      await titleEl.hover();
      await page.getByTestId('overview-title-edit-affordance').click();

      const input = page.getByTestId('overview-title-input');
      await expect(input).toBeVisible();

      // Track that no PUT against /title is fired during this flow.
      let putFired = false;
      const offPut = (resp: import('@playwright/test').Response) => {
        if (/\/api\/tasks\/.+\/title/.test(resp.url()) && resp.request().method() === 'PUT') {
          putFired = true;
        }
      };
      page.on('response', offPut);

      await input.fill(`Should not stick ${id}`);
      await input.press('Escape');

      // Editor closes and the original title is preserved.
      await expect(input).not.toBeVisible();
      await expect(titleEl).toHaveText(new RegExp(title));

      // The detail panel must stay open — Escape must not bubble past
      // the local edit cancel.
      await expect(page.getByTestId('detail-panes')).toBeVisible();

      // Brief settle window so any pending PUT would have surfaced.
      await page.waitForTimeout(200);
      page.off('response', offPut);
      expect(putFired).toBe(false);

      // Backend still carries the original title.
      const fresh = await api<{ info: { title: string } }>(
        `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(wp.path)}`,
      );
      expect(fresh.info.title).toBe(title);
    } finally {
      await deleteJob(id, wp.path);
    }
  });
});
