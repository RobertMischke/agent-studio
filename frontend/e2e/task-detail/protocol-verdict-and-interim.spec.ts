import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';

interface WatchPathEntry { path: string; name: string }

interface JobDetail {
  info: { id: string; watchPath: string };
  statusMarkdown: string | null;
}

function buildCompletedJobDetail(jobId: string, watchPath: string, statusMarkdown: string | null) {
  return {
    info: {
      id: jobId,
      taskKey: `${watchPath}::${jobId}`,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Verdict duration spec fixture',
      state: '5-human-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/5-human-review/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      orchestratorVerdict: null,
      order: 1,
    },
    promptMarkdown: 'Pretend prompt.',
    statusMarkdown,
    log: [],
    promptHistory: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installCompletedJobMocks(
  page: Page,
  target: { id: string; watchPath: string },
  statusMarkdown: string,
): Promise<void> {
  const detailBody = JSON.stringify(buildCompletedJobDetail(target.id, target.watchPath, statusMarkdown));

  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: detailBody });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/output?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/runs?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/session-events?**`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/claude-session?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(null) });
  });
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

    // The banner root carries `role="status"`; scope to it so the newer
    // sub-element testids (protocol-verdict-detail / -duration / -superseded)
    // do not turn this into a multi-match locator.
    const chip = page.locator('[data-testid^="protocol-verdict-"][role="status"]');
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

  test('duration lifts out of the # Status section into a chip on the pill', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });

    const jobs = await listJobs();
    test.skip(jobs.length === 0, 'No jobs available in workspace');
    const target = { id: jobs[0].id, watchPath: jobs[0].watchPath };

    const statusMarkdown = [
      '# Status',
      '',
      '- Result: Success',
      '- Duration: 4 min',
      '',
      '## What Was Done',
      '- Refactored the verdict pill so duration rides on the chip.',
      '',
      '## Notes',
      '- Status header is now lifted out of the rendered body.',
    ].join('\n');

    await installCompletedJobMocks(page, target, statusMarkdown);

    await page.goto(
      `/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`,
    );

    const chip = page.getByTestId('protocol-verdict-ok');
    await expect(chip).toBeVisible({ timeout: 15_000 });

    const duration = page.getByTestId('protocol-verdict-duration');
    await expect(duration).toBeVisible();
    await expect(duration).toContainText('4 min');
    // Icon affordance: a clock glyph sits before the value.
    await expect(duration).toContainText('⏱');

    // The Status section (heading + Result/Duration list) must not appear in
    // the rendered markdown body — that is the whole point of the collapse.
    const body = page.getByTestId('protocol-beautiful-results');
    await expect(body).toBeVisible();
    await expect(body).not.toContainText('Duration:');
    await expect(body).not.toContainText('Result: Success');
    // Sibling sections still render so we know the body itself is alive.
    await expect(body).toContainText('What Was Done');
    await expect(body).toContainText('Refactored the verdict pill');

    await page.screenshot({
      path: 'test-results/protocol-verdict-duration-chip.png',
      fullPage: false,
    });
  });

  test('review activity without a verdict stays actionable from Result', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1600, height: 1100 });

    const jobs = await listJobs();
    test.skip(jobs.length === 0, 'No jobs available in workspace');
    const target = { id: jobs[0].id, watchPath: jobs[0].watchPath };
    const detail = buildCompletedJobDetail(target.id, target.watchPath, null);
    detail.summaryState = {
      status: 'none',
      startedAt: null,
      finishedAt: null,
      errorMessage: null,
    };

    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/summary/regenerate?**`, async (route) => {
      await route.fulfill({ status: 202, contentType: 'application/json', body: '{}' });
    });
    await page.route((url) => url.pathname === `/api/tasks/${encodeURIComponent(target.id)}`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/output?**`, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          {
            timestamp: '2026-07-12T10:00:00Z',
            stream: 'stdout',
            text: 'Investigated the review hand-off and left useful activity.',
          },
        ]),
      });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/runs?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/session-events?**`, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ events: [], sessionChain: [] }),
      });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/claude-session?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(null) });
    });

    await page.goto(
      `/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`,
    );

    const resultTab = page.getByTestId('inspector-tab-protocol');
    await expect(resultTab).toBeVisible({ timeout: 15_000 });
    await expect(resultTab).toBeEnabled();
    await expect(resultTab).toHaveClass(/pane-tab--active/);

    const generate = page.getByTestId('protocol-regenerate-summary');
    await expect(generate).toBeVisible();
    await expect(generate).toBeEnabled();
    await expect(generate).toContainText('Generate result');
    await testInfo.attach('verdictless-review-result', {
      body: await page.getByTestId('pane-protocol').screenshot(),
      contentType: 'image/png',
    });

    const request = page.waitForRequest((candidate) =>
      candidate.method() === 'POST' && candidate.url().includes('/summary/regenerate'),
    );
    await generate.evaluate((element: HTMLButtonElement) => element.click());
    await request;
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
        `${BACKEND}/api/tasks/${encodeURIComponent(created.id)}/summary/interim?watchPath=${encodeURIComponent(watchPath)}`,
        { headers: { 'x-client-id': 'local-default' } }
      );
      // The endpoint surfaces precondition errors as 400 with `{ error: "..." }`.
      expect(res.status()).toBe(400);
      const body = await res.json();
      expect(body.error).toMatch(/CLI output|cli-output\.log/i);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      ).catch(() => { /* best-effort cleanup */ });
    }
  });
});
