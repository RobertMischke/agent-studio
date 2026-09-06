import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import type { Page, TestInfo } from '@playwright/test';
import { expect, test } from '../fixtures/dev-backend';
import { createJob, getJob, moveJob } from '../helpers/jobs';

const REVIEW_REASON =
  'documentation-impact blocked: Public API and state-file contract changed without corresponding load-bearing doc updates.';

async function deleteJob(jobId: string, watchPath: string, baseUrl: string): Promise<void> {
  await fetch(`${baseUrl}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': 'local-default' },
  }).catch(() => undefined);
}

async function capture(page: Page, testInfo: TestInfo, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate((value) => {
    document.documentElement.setAttribute('data-studio-theme', value);
    localStorage.setItem('atp.studio.theme', value);
  }, theme);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
  const name = `test-evidence-independent-review--mocked-${theme}.png`;
  const body = await page.screenshot({ fullPage: false });
  await testInfo.attach(name, { body, contentType: 'image/png' });
  if (process.env.JOB_RESULTS_DIR) {
    await mkdir(process.env.JOB_RESULTS_DIR, { recursive: true });
    await writeFile(join(process.env.JOB_RESULTS_DIR, name), body);
  }
}

test('Evidence renders passing build proof independently from a blocked review aspect', async ({ page, devBackend }, testInfo) => {
  const watchPaths = await fetch(`${devBackend.baseUrl}/api/watch-paths`, {
    headers: { 'x-client-id': 'local-default' },
  }).then(response => response.json() as Promise<Array<{ path: string }>>);
  const watchPath = watchPaths[0]?.path;
  expect(watchPath).toBeTruthy();

  const created = await createJob({
    title: `independent review evidence ${Date.now()}`,
    watchPath,
    targetState: '2-ready',
  });
  await moveJob(created.id, watchPath, '5-human-review');
  const job = await getJob(created.id, watchPath);

  await page.route(`**/api/tasks/${encodeURIComponent(created.id)}?**`, async route => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }
    const response = await route.fetch();
    const detail = await response.json();
    detail.info.testEvidence = {
      runId: null,
      runCommit: null,
      runState: null,
      runResult: null,
      matchQuality: 'perfect',
      direction: 'exact',
      distance: 0,
      diffContained: true,
      evidenceState: 'proven',
      awaitingEvidence: false,
      summary: 'Review build-tests Pass at 491ddd64 (verify-1, verify-2)',
      sources: [
        {
          kind: 'review-build-tests',
          id: 'review_ad5cca8e3178425fb9ba9cabe329d50e',
          commit: '491ddd64',
          result: 'passed',
          observedAt: '2026-08-31T20:41:22Z',
          summary: 'Review build-tests Pass at 491ddd64 (verify-1, verify-2)',
          reason: 'verify-1 and verify-2 passed.',
          reportRef: 'remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md',
        },
        {
          kind: 'review-aspects',
          id: 'review_ad5cca8e3178425fb9ba9cabe329d50e:documentation-impact',
          commit: '491ddd64',
          result: 'blocked',
          observedAt: '2026-08-31T20:41:22Z',
          summary: 'Review blocked by documentation-impact',
          reason: REVIEW_REASON,
          reportRef: 'remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md',
        },
      ],
    };
    await route.fulfill({ response, json: detail });
  });

  try {
    await page.route('**/api/auth/status', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'networked',
        bootstrapRequired: false,
        authenticated: true,
        user: { username: 'playwright', role: 'operator' },
      }),
    }));
    await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`);
    const dismissRecovery = page.getByTestId('crash-recovery-dismiss-all');
    await dismissRecovery.waitFor({ state: 'visible', timeout: 1_500 }).catch(() => undefined);
    if (await dismissRecovery.isVisible().catch(() => false)) await dismissRecovery.click();
    await page.getByTestId('prompt-tab-evidence').click();

    const evidenceStatus = page.getByTestId('evidence-tab-test-evidence');
    const build = evidenceStatus.getByTestId('test-evidence-source-review-build-tests');
    const aspect = evidenceStatus.getByTestId('test-evidence-source-review-aspects');
    await expect(build).toHaveAttribute('data-tone', 'good');
    await expect(build).toContainText('Review build-tests Pass at 491ddd64 (verify-1, verify-2)');
    await expect(build).toContainText('verify-1 and verify-2 passed.');
    await expect(aspect).toHaveAttribute('data-tone', 'warn');
    await expect(aspect).toContainText('Review blocked by documentation-impact');
    await expect(aspect).toContainText(REVIEW_REASON);
    await expect(aspect).toHaveAttribute('aria-label', `Review blocked by documentation-impact. ${REVIEW_REASON}`);

    const reportLink = aspect.getByRole('link', { name: `Open report. ${REVIEW_REASON}` });
    await expect(reportLink).toHaveAttribute(
      'href',
      new RegExp(`/api/tasks/${created.id}/files/remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e\\.md\\?`),
    );
    await aspect.hover();
    await expect(page.getByRole('tooltip')).toHaveText(REVIEW_REASON);

    await capture(page, testInfo, 'light');
    await capture(page, testInfo, 'dark');
  } finally {
    await deleteJob(job.id, watchPath, devBackend.baseUrl);
  }
});
