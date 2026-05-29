import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

/**
 * Regression spec for the operator complaint:
 *
 *   "Auf dem Overview vom Task-Detail kann ich das Modell nicht aendern.
 *    Der Dialog geht zwar auf, aber ich kann es nicht setzen."
 *
 * The Overview tab renders the same <app-chat-model-badge> as the chat
 * composer. The badge's Done button must drive the canonical
 * `PUT /api/jobs/{id}/model` round-trip via the overview-pane ->
 * prompt-pane -> task-detail event forwarding chain, AND the rendered
 * badge text must reflect the new model after the parent re-fetches
 * the detail (no manual reload required).
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/jobs/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE',
    });
  } catch { /* best-effort cleanup */ }
}

async function activateActivityTab(page: Page): Promise<void> {
  const activityTab = page.getByTestId('inspector-tab-activity');
  if (await activityTab.isVisible().catch(() => false)) {
    await activityTab.click();
  }
}

test.describe('Overview tab — model picker', () => {
  test('clicking a model in the Overview picker persists immediately', async ({ page }) => {
    // Operator expectation, per the bug report: "Klick anderes Modell ->
    // Overview zeigt neuen Wert + reload bestaetigt". The picker must
    // auto-commit on model click when no CLI change is pending - no Done
    // click required. The atomic-Done flow is reserved for CLI switches.
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `overview-model-change-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# overview model change',
      targetState: '2-ready',
    });

    try {
      // Wait for the response (not just the request) so the backend has
      // actually written the new model before we poll for it.
      const modelPutResponsePromise = page.waitForResponse((res) =>
        res.request().method() === 'PUT' &&
        /\/api\/jobs\/.+\/model(\?|$)/.test(res.url()),
      );

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await activateActivityTab(page);

      // Scope to the Overview-tab Agent block so we don't accidentally
      // grab the chat-composer badge that lives in the protocol pane.
      const overviewBadge = page.getByTestId('overview-agent').getByTestId('chat-compose-model');
      await expect(overviewBadge).toBeVisible({ timeout: 10_000 });
      await expect(overviewBadge).toContainText(/opus\s+4\.7/i);

      await overviewBadge.click();

      const picker = page.getByTestId('overview-agent').getByTestId('chat-model-picker');
      await expect(picker).toBeVisible({ timeout: 5_000 });

      // Click a different model. Auto-commit fires; picker closes.
      const sonnetPill = page
        .getByTestId('overview-agent')
        .getByTestId('chat-model-picker-model-claude-sonnet-4-6');
      await expect(sonnetPill).toBeVisible();
      await sonnetPill.click();
      await expect(picker).toBeHidden({ timeout: 5_000 });

      // PUT round-trip completes successfully (200) before we check the
      // persisted state. Without this gate the GET races the PUT and may
      // see the pre-change snapshot.
      const res = await modelPutResponsePromise;
      expect(res.url()).toContain(`/api/jobs/${encodeURIComponent(job.id)}/model`);
      expect(res.status()).toBe(200);

      // Overview badge re-renders to the new model. The operator-visible
      // bit: an open Overview tab updates without a manual reload.
      await expect(overviewBadge).toContainText(/sonnet\s+4\.6/i, { timeout: 10_000 });

      // And the backend actually persisted it.
      const persisted = await getJob(job.id, watchPath);
      expect(persisted.model).toBe('claude-sonnet-4-6');
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});
