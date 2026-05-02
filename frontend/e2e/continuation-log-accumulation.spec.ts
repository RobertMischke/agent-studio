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

    // Switch to Trace mode (post-revamp equivalent of Raw) for a reliable
    // line-count check. Trace shows every parsed group with all its lines.
    const traceBtn = page.getByTestId('activity-log-mode-trace');
    await expect(traceBtn).toBeVisible({ timeout: 5_000 });
    await traceBtn.click();

    const body = page.getByTestId('activity-log-body');
    await expect(body).toBeVisible();

    // At least one trace group must be visible - if the protocol were reset,
    // this container would be empty.
    const groups = body.locator('.trace-group');
    await expect(groups.first()).toBeVisible({ timeout: 5_000 });
    const visibleCount = await groups.count();
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

    // Trace mode: per-group view. Verify the rendered group count is in the
    // same order of magnitude as the API line count - the goal of this test
    // is to catch a regression where the UI silently truncates historical
    // log content, not to assert exact equality (the parser groups multiple
    // raw lines per visible item, so a 1:1 line comparison would never hold).
    await page.getByTestId('activity-log-mode-trace').click();

    const body = page.getByTestId('activity-log-body');
    await expect(body.locator('.trace-group').first()).toBeVisible({ timeout: 5_000 });

    const groupCount = await body.locator('.trace-group').count();
    expect(groupCount).toBeGreaterThan(0);
    // Sanity: a non-trivial run produces well under one trace-group per API
    // line (groups bundle batches of reads, etc.) but at least 1 group per
    // ~50 raw lines is a reasonable lower bound. This catches the
    // "everything truncated to a single placeholder" regression while
    // tolerating the parser's compression behaviour.
    expect(groupCount).toBeGreaterThanOrEqual(Math.max(1, Math.floor(apiLineCount / 50)));

    await body.screenshot({ path: 'continuation-log-accumulation-count.png' });
  });
});
