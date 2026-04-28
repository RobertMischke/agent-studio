import { test, expect } from '@playwright/test';
import { listJobs } from './helpers/jobs';
import { api } from './helpers/api';

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
 * Activity log — chat-window style view.
 *
 * The activity log offers three modes: Chat (Copilot/Cursor-style bubbles
 * with avatars), Parsed (collapsible groups), and Raw (line-by-line).
 * Toggling Chat must render bubbles with role data and group tool calls.
 */
test.describe('Activity log — chat mode', () => {
  test('chat mode renders message bubbles with agent and tool roles', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByRole('button', { name: 'Activity', exact: true });
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const chatModeBtn = page.getByTestId('activity-log-mode-chat');
    await expect(chatModeBtn).toBeVisible({ timeout: 5_000 });
    await chatModeBtn.click();

    const chat = page.getByTestId('activity-log-chat');
    await expect(chat).toBeVisible();

    // At least one message bubble should render.
    const bubbles = chat.locator('.chat-msg');
    await expect(bubbles.first()).toBeVisible({ timeout: 5_000 });
    const count = await bubbles.count();
    expect(count).toBeGreaterThan(0);

    // Each bubble has a role attribute that should be one of the known values.
    const roles = await bubbles.evaluateAll((els) => els.map((el) => el.getAttribute('data-role')));
    for (const r of roles) {
      expect(['agent', 'tool', 'system']).toContain(r);
    }

    // Capture a screenshot of the chat view for visual inspection.
    // Saved to a fixed path so it survives Playwright's per-test cleanup.
    // Use the bounded body container so the image is one viewport tall, not
    // the full scroll height (which can be tens of thousands of pixels for
    // long jobs).
    const body = page.getByTestId('activity-log-body');
    // Scroll up a few viewport heights so the screenshot shows agent and
    // tool-call bubbles, not just the trailing System ERROR.
    await body.evaluate((el) => { el.scrollTop = Math.max(0, el.scrollHeight - el.clientHeight - 800); });
    await page.waitForTimeout(150);
    await body.screenshot({ path: 'activity-log-chat-mode.png' });
  });

  test('switching back to Parsed restores the group view', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByRole('button', { name: 'Activity', exact: true }).click();

    await page.getByTestId('activity-log-mode-chat').click();
    await expect(page.getByTestId('activity-log-chat')).toBeVisible();

    await page.getByTestId('activity-log-mode-parsed').click();
    await expect(page.getByTestId('activity-log-chat')).toHaveCount(0);
    // Parsed mode shows the activity-group elements.
    await expect(page.locator('.activity-group').first()).toBeVisible({ timeout: 5_000 });
  });
});
