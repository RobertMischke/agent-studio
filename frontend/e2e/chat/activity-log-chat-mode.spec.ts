import { test, expect } from '@playwright/test';
import { listJobs } from '../helpers/jobs';
import { api } from '../helpers/api';

async function findJobWithOutput(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  let best: { id: string; watchPath: string } | null = null;
  let bestLines = 0;
  for (const j of jobs) {
    try {
      const out = await api<unknown[]>(`/api/jobs/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`);
      if (Array.isArray(out) && out.length > bestLines) {
        bestLines = out.length;
        best = { id: j.id, watchPath: j.watchPath };
      }
    } catch { /* ignore */ }
  }
  return bestLines > 0 ? best : null;
}

/**
 * Activity log — Conversation/Trace modes.
 *
 * The activity log offers two modes: Conversation (chat-style with grouped
 * agent text and collapsible tool bursts) and Trace (chronological dump for
 * debugging). The redesign replaced the prior 3-mode (chat/parsed/raw) UI
 * because the per-kind filter checkboxes added more friction than value.
 */
test.describe('Activity log — conversation mode', () => {
  test('conversation mode renders agent / user / tool turns', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const conversationBtn = page.getByTestId('activity-log-mode-conversation');
    await expect(conversationBtn).toBeVisible({ timeout: 5_000 });
    await conversationBtn.click();

    const convo = page.getByTestId('activity-log-conversation');
    await expect(convo).toBeVisible();

    // At least one turn should render.
    const turns = convo.locator('.convo-turn');
    await expect(turns.first()).toBeVisible({ timeout: 5_000 });
    const count = await turns.count();
    expect(count).toBeGreaterThan(0);

    // Capture a screenshot for visual inspection.
    const body = page.getByTestId('activity-log-body');
    await body.evaluate((el) => { el.scrollTop = Math.max(0, el.scrollHeight - el.clientHeight - 800); });
    await page.waitForTimeout(150);
    await body.screenshot({ path: 'activity-log-conversation-mode.png' });
  });

  test('switching to Trace restores the per-group view', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByTestId('inspector-tab-activity').click();

    await page.getByTestId('activity-log-mode-conversation').click();
    await expect(page.getByTestId('activity-log-conversation')).toBeVisible();

    await page.getByTestId('activity-log-mode-trace').click();
    await expect(page.getByTestId('activity-log-conversation')).toHaveCount(0);
    await expect(page.getByTestId('activity-log-trace')).toBeVisible();
    await expect(page.locator('.trace-group').first()).toBeVisible({ timeout: 5_000 });
  });
});
