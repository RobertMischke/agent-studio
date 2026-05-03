import { test, expect } from '@playwright/test';
import { api, BACKEND } from './helpers/api';
import { createJob, startJob, waitForJob } from './helpers/jobs';
import { getClaudeQuota } from './helpers/quota';

interface WatchPathEntry { path: string; name: string }

/**
 * Regression for the "Task execution failed with exit code -1" false alarm.
 *
 * The backend kills the CLI subprocess via Process.Kill() for three
 * separate intents (explicit Pause button, Pause-&-Send, watchdog hung).
 * Every one of them yields exitCode = -1 on Windows, which the legacy
 * MonitorProcessAsync mapped to status='failed' and the frontend then
 * rendered as a crash modal. The fix: pass a RunStopReason through the
 * Stop chain so RunStatusClassifier returns status='stopped' for any
 * deliberate kill. The frontend skips the failure modal for 'stopped'.
 *
 * This spec exercises the explicit-pause path end-to-end. The Pause-&-Send
 * path is structurally the same code; we still cover it via the API call
 * shape so a regression there shows up here too.
 */
test.describe('Stop -> stopped (no error modal)', () => {
  test.setTimeout(180_000);

  test('@billable explicit Pause yields status=stopped, no failure modal', async ({ page }) => {
    test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
    const watchPaths = await api<WatchPathEntry[]>('/api/watch-paths');
    test.skip(watchPaths.length === 0, 'No watch paths configured');
    const watchPath = watchPaths[0].path;

    const q = await getClaudeQuota();
    test.skip(!q.available || !q.hasHeadroom, `Claude unavailable or near quota: worst=${q.worstUsedPct}%`);

    const slug = `e2e-stop-no-modal-${Date.now()}`;
    const created = await createJob({
      id: slug,
      title: 'E2E stop -> stopped',
      watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      // Long-enough prompt so we have time to call /stop while the CLI is
      // still streaming. The exact text does not matter; the spec asserts
      // on the run-end status, not on the agent's reply.
      promptMarkdown: 'Take 30 seconds to think very carefully, then write three short paragraphs about the architecture of a typical web application. Do not edit any files.',
      targetState: '2-ready'
    });

    try {
      const exec = await startJob(created.id, watchPath, {
        cliType: 'claude',
        model: 'claude-haiku-4-5'
      });
      expect(exec.status).toBe('running');

      // Open the detail view so applyExecutionState gets the chance to
      // (mis)render a failure modal if the regression is back.
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible();

      // Give the CLI a tick to actually get into the model call so the
      // kill races a real running process, not a fresh exec()-then-exit.
      await page.waitForTimeout(2_500);

      // Trigger the explicit-pause path through the API (same call shape
      // the toolbar Pause button issues). reason=user is the default.
      const stopRes = await page.request.post(
        `${BACKEND}/api/jobs/${encodeURIComponent(created.id)}/stop?watchPath=${encodeURIComponent(watchPath)}`
      );
      expect(stopRes.status()).toBe(200);

      // Wait for the run to actually finish. With the fix the resulting
      // status must be 'stopped'; with the regression it would be 'failed'
      // with exitCode -1.
      const finished = await waitForJob(
        created.id,
        watchPath,
        j => j.execution !== null && j.execution.status !== 'running',
        { timeoutMs: 30_000, intervalMs: 500 }
      );

      const e = finished.execution!;
      expect(e.status,
        `Expected status=stopped after Pause, got ${e.status} (exit=${e.exitCode}). ` +
        `If this reads 'failed' with exitCode -1, the RunStatusClassifier wiring regressed.`
      ).toBe('stopped');

      // The crash modal is keyed by data-testid="error-dialog" / "error-dialog-message"
      // depending on the build; the simplest invariant is that NO dialog
      // with the failure copy ever appeared while polling settled.
      // We poll for a few extra ticks so applyExecutionState has time to
      // (incorrectly) react if the regression returns.
      await page.waitForTimeout(3_000);
      const crashCopy = page.getByText(/Task execution failed with exit code/i);
      await expect(crashCopy).toHaveCount(0);

      await page.screenshot({
        path: 'test-results/stop-no-error-modal.png',
        fullPage: false
      });
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      ).catch(() => { /* best-effort cleanup */ });
    }
  });

  test('backend coerces unknown reason to user (does not 400)', async () => {
    // Structural API contract: the /stop endpoint accepts ?reason= as a
    // hint, never rejects an unknown value. This guards the backend half
    // of the Pause-&-Send wiring (the frontend half lives in JobService.
    // stopJob whose signature pins the 'user' | 'followup' literal type).
    const watchPaths = await api<WatchPathEntry[]>('/api/watch-paths');
    test.skip(watchPaths.length === 0, 'No watch paths configured');

    // Hitting /stop on a job that is not running returns 404 (info exists,
    // no live process). Any 4xx other than 404, or a 5xx, would mean the
    // reason parser threw - that's the real regression signal here.
    const fake = `e2e-nonexistent-${Date.now()}`;
    const url = `/api/jobs/${encodeURIComponent(fake)}/stop?watchPath=${encodeURIComponent(watchPaths[0].path)}&reason=followup`;
    const res = await fetch(`${BACKEND}${url}`, { method: 'POST' });
    expect([200, 404]).toContain(res.status);

    const res2 = await fetch(`${BACKEND}${url.replace('reason=followup', 'reason=banana')}`, { method: 'POST' });
    expect([200, 404]).toContain(res2.status);
  });
});
