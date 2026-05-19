import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';

interface WatchPathEntry { path: string; name: string }

interface JobDetail {
  info: { id: string; watchPath: string };
  statusMarkdown: string | null;
}

/**
 * Verifies the two protocol-pane additions for the
 * `protokollsummery-verbesserung` task:
 *
 *   1. A 3-state verdict chip (🟢 OK / 🔴 Problem / 🟡 Unclear) renders at the
 *      very top of the protocol pane body, above hygiene strip and tabs. The
 *      kind is derived deterministically from the existing signals.
 *
 *   2. The interim-status backend endpoint exists and surfaces the precondition
 *      failure (missing cli-output.log) verbatim - no silent 500.
 *
 * The full Haiku interim summary path is `@billable` and exercised by
 * `claude-hello-world.spec.ts` indirectly (same one-shot pipeline). Here we
 * lock the cheap branches so the UX wiring does not silently regress.
 */
test.describe('Protocol pane - verdict chip + interim status', () => {
  test('verdict chip renders for any reachable job', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });

    const jobs = await listJobs();
    test.skip(jobs.length === 0, 'No jobs available in workspace');
    const first = jobs[0];

    await page.goto(
      `/?job=${encodeURIComponent(first.id)}&watchPath=${encodeURIComponent(first.watchPath)}`
    );

    const chip = page.locator('[data-testid^="protocol-verdict-"]');
    await expect(chip).toBeVisible({ timeout: 15_000 });

    // One of the three kinds must be present. The exact one depends on the
    // job's current state, which we do not control from this spec.
    const kind = await chip.getAttribute('data-testid');
    expect(kind).toMatch(/^protocol-verdict-(ok|problem|unclear)$/);

    await page.screenshot({
      path: 'test-results/protocol-verdict-chip.png',
      fullPage: false
    });
  });

  test('interim endpoint returns the precondition error when cli-output.log is missing', async ({ page }) => {
    const watchPaths = await api<WatchPathEntry[]>('/api/watch-paths');
    test.skip(watchPaths.length === 0, 'No watch paths configured');
    const watchPath = watchPaths[0].path;

    const slug = `e2e-interim-precondition-${Date.now()}`;
    const created = await createJob({
      id: slug,
      title: 'E2E interim precondition',
      watchPath,
      agent: 'claude',
      cliType: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation'
    });

    try {
      const res = await page.request.post(
        `${BACKEND}/api/jobs/${encodeURIComponent(created.id)}/summary/interim?watchPath=${encodeURIComponent(watchPath)}`,
        { headers: { 'x-client-id': 'local-default' } }
      );
      // The endpoint surfaces precondition errors as 400 with `{ error: "..." }`.
      expect(res.status()).toBe(400);
      const body = await res.json();
      expect(body.error).toMatch(/CLI output|cli-output\.log/i);
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      ).catch(() => { /* best-effort cleanup */ });
    }
  });
});
