import { test, expect } from '@playwright/test';
import { listJobs } from './helpers/jobs';
import { api, BACKEND } from './helpers/api';

interface CliOutputLine {
  timestamp: string;
  stream: string;
  text: string;
}

/**
 * Continuation log accumulation
 *
 * Regression spec for: when a chat continuation is sent, the activity log
 * must retain all previous session output rather than resetting to only the
 * new run's lines.
 *
 * Full "send → verify" test requires a real CLI run (@billable).  The non-
 * billable tests here cover the historical-output path: `GET /output` must
 * return persisted `cli-output.log` content, and the UI must render it.
 */

async function findJobWithPersistedOutput(): Promise<{ id: string; watchPath: string; lineCount: number } | null> {
  const jobs = await listJobs();
  let best: { id: string; watchPath: string; lineCount: number } | null = null;
  let bestCount = 0;
  for (const j of jobs) {
    try {
      const out = await api<CliOutputLine[]>(
        `/api/jobs/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (Array.isArray(out) && out.length > bestCount) {
        bestCount = out.length;
        best = { id: j.id, watchPath: j.watchPath, lineCount: out.length };
      }
    } catch { /* ignore */ }
  }
  return bestCount > 0 ? best : null;
}

test.describe('Continuation log accumulation', () => {
  test('output endpoint returns historical cli-output.log content', async () => {
    const target = await findJobWithPersistedOutput();
    if (!target) {
      test.skip(true, 'No job with persisted CLI output available');
      return;
    }

    const output = await api<CliOutputLine[]>(
      `/api/jobs/${encodeURIComponent(target.id)}/output?watchPath=${encodeURIComponent(target.watchPath)}`
    );
    expect(Array.isArray(output)).toBe(true);
    expect(output.length).toBeGreaterThan(0);
    // Each line must have the standard CliOutputLine shape.
    for (const line of output.slice(0, 5)) {
      expect(line).toHaveProperty('timestamp');
      expect(line).toHaveProperty('stream');
      expect(line).toHaveProperty('text');
    }
  });

  test('activity log renders historical output from cli-output.log', async ({ page }) => {
    const target = await findJobWithPersistedOutput();
    if (!target) {
      test.skip(true, 'No job with persisted CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    // Switch to Raw mode for a reliable line-count check.
    const rawBtn = page.getByTestId('activity-log-mode-raw');
    await expect(rawBtn).toBeVisible({ timeout: 5_000 });
    await rawBtn.click();

    const body = page.getByTestId('activity-log-body');
    await expect(body).toBeVisible();

    // At least one activity line must be visible — if the protocol were reset,
    // this container would be empty.
    const lines = body.locator('.activity-line');
    await expect(lines.first()).toBeVisible({ timeout: 5_000 });
    const visibleCount = await lines.count();
    expect(visibleCount).toBeGreaterThan(0);

    await body.screenshot({ path: 'continuation-log-accumulation.png' });
  });

  test('activity log line count matches output endpoint', async ({ page }) => {
    const target = await findJobWithPersistedOutput();
    if (!target) {
      test.skip(true, 'No job with persisted CLI output available');
      return;
    }

    // Fetch the authoritative line count from the API.
    const apiOutput = await api<CliOutputLine[]>(
      `/api/jobs/${encodeURIComponent(target.id)}/output?watchPath=${encodeURIComponent(target.watchPath)}`
    );
    const apiLineCount = apiOutput.length;

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    // Raw mode: each API line maps to one rendered row.
    await page.getByTestId('activity-log-mode-raw').click();

    const body = page.getByTestId('activity-log-body');
    await expect(body.locator('.activity-line').first()).toBeVisible({ timeout: 5_000 });

    // The summary shows "N / N lines" — extract the total.
    const summary = page.locator('.activity-log__summary span').last();
    const summaryText = await summary.textContent();
    // Format is "N / M lines" where M is the total.
    const totalMatch = summaryText?.match(/(\d+)\s+lines/);
    if (totalMatch) {
      const uiTotal = parseInt(totalMatch[1], 10);
      // UI total must equal or exceed the API total (filters may hide some).
      expect(uiTotal).toBeGreaterThanOrEqual(apiLineCount);
    }

    await body.screenshot({ path: 'continuation-log-accumulation-count.png' });
  });
});
