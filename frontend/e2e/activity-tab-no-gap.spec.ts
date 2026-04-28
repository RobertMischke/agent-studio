import { test, expect } from '@playwright/test';
import { listJobs } from './helpers/jobs';
import { api } from './helpers/api';

async function findJobWithOutput(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  let best: { id: string; watchPath: string; n: number } | null = null;
  for (const j of jobs) {
    try {
      const out = await api<unknown[]>(
        `/api/jobs/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (Array.isArray(out) && out.length > 0 && (!best || out.length > best.n)) {
        best = { id: j.id, watchPath: j.watchPath, n: out.length };
      }
    } catch { /* ignore */ }
  }
  return best ? { id: best.id, watchPath: best.watchPath } : null;
}

/**
 * Activity tab — there should be no large empty gap between the activity log
 * body and the chat compose strip. Regression coverage for the layout where
 * the log body was hard-capped at 34vh, leaving the chat input pushed away
 * from the log content by a tall blank area.
 */
test.describe('Activity tab — no gap between log and compose', () => {
  test('chat compose sits flush below the activity log body', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(
      `/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`
    );

    const activityTab = page.getByRole('button', { name: 'Activity', exact: true });
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const body = page.getByTestId('activity-log-body');
    const compose = page.getByTestId('activity-chat-compose');
    await expect(body).toBeVisible({ timeout: 5_000 });
    await expect(compose).toBeVisible();

    await page.screenshot({ path: 'test-results/activity-tab-no-gap.png', fullPage: false });

    const bodyBox = await body.boundingBox();
    const composeBox = await compose.boundingBox();
    if (!bodyBox || !composeBox) throw new Error('missing bounding boxes');

    const gap = composeBox.y - (bodyBox.y + bodyBox.height);
    // Allow up to 32px breathing room (margin-top on .chat-compose is 10px).
    expect(gap).toBeLessThan(32);
  });
});
