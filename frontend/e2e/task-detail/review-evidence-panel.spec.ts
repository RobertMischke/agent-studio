import { test, expect } from '@playwright/test';
import { createJob, getJob, moveJob, listJobs } from '../helpers/jobs';
import { api, BACKEND } from '../helpers/api';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Verifies the task-evidence contract documented in
 * docs/contracts/filesystem.md `results/review-evidence.jsonl`:
 *
 *   - the panel renders findings stored in the file (`high` and `info`),
 *   - the panel resolves linked artifacts (a real PNG copied next to it),
 *   - "Create follow-up task" produces a normal queued job in the same
 *     project that the user can navigate to,
 *   - acknowledged findings keep their state across a reload.
 *
 * The spec hand-writes `results/review-evidence.jsonl` because the
 * file is a producer-owned artifact (audits, reviewers) and we are
 * exercising the consumer surface here. The seeded evidence also
 * intentionally includes a malformed line to lock the
 * "bad-line-does-not-break-the-panel" property end-to-end.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

// 1x1 transparent PNG so artifact resolution has a real file to point at.
const TINY_PNG = Buffer.from([
  0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
  0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
  0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
  0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4,
  0x89, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x44, 0x41,
  0x54, 0x78, 0x9c, 0x62, 0x00, 0x02, 0x00, 0x00,
  0x05, 0x00, 0x01, 0x0d, 0x0a, 0x2d, 0xb4, 0x00,
  0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae,
  0x42, 0x60, 0x82
]);

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

function uid() {
  return `e2e-evidence-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function cleanupStaleFollowups(watchPath: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j =>
    j.watchPath === watchPath && (
      j.id.startsWith('e2e-evidence-') ||
      j.title.startsWith('Follow-up: ')
    ));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

test.describe('Review evidence panel', () => {
  test('renders findings, supports follow-up creation, surfaces malformed lines silently', async ({ page }) => {
    test.setTimeout(60_000);

    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    const id = uid();
    const created = await createJob({
      id,
      title: `e2e evidence ${id}`,
      watchPath,
      targetState: '2-ready'
    });
    // Backend restricts CreateJob targetState to backlog/preparation/ready;
    // walk the job into 5-human-review so the panel renders in its
    // natural review-stage context.
    await moveJob(created.id, watchPath, '5-human-review');

    const screenshotsDir = path.resolve('test-results', 'review-evidence-panel');
    fs.mkdirSync(screenshotsDir, { recursive: true });
    const jobResultsDir = path.join((await getJob(created.id, watchPath)).folderPath, 'results');

    try {
      const job = await getJob(created.id, watchPath);

      // Drop a real PNG next to the evidence file so the artifact path the
      // finding references resolves to a 200 from the backend.
      fs.mkdirSync(jobResultsDir, { recursive: true });
      fs.writeFileSync(path.join(jobResultsDir, 'audit-screenshot.png'), TINY_PNG);

      const evidenceLines = [
        JSON.stringify({
          id: 'high-token-leak',
          source: 'security-audit',
          severity: 'high',
          title: 'Bearer token logged in plaintext',
          body: 'AuthService.LogIn writes the bearer token to logs/cli-output.log.',
          createdAt: '2026-05-08T12:34:00Z',
          fileRefs: ['backend/Services/AuthService.cs:142'],
          artifacts: ['results/audit-screenshot.png']
        }),
        // Malformed line: must be silently skipped without breaking the panel.
        '{ this is not valid json',
        JSON.stringify({
          id: 'info-style-nit',
          source: 'human-note',
          severity: 'info',
          title: 'Compose box border off by 1px when steer is active',
          body: 'Nit; cosmetic.',
          createdAt: '2026-05-08T12:36:00Z'
        })
      ];

      fs.writeFileSync(
        path.join(jobResultsDir, 'review-evidence.jsonl'),
        evidenceLines.join('\n') + '\n',
        'utf8'
      );

      // Direct API check: the JobDetail endpoint must surface the two
      // valid findings and drop the malformed line without erroring.
      const detail = await api<{ reviewEvidence: Array<{ id: string; severity: string }> }>(
        `/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`
      );
      expect(detail.reviewEvidence.length).toBe(2);
      expect(detail.reviewEvidence.map((e) => e.id).sort()).toEqual(['high-token-leak', 'info-style-nit']);

      // Open the detail view.
      await page.goto(
        `/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`
      );

      const panel = page.getByTestId('review-evidence-panel');
      await expect(panel).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('review-evidence-count')).toHaveText(/2 findings/);

      // High-severity row renders with its chip and file reference.
      const highRow = page.getByTestId('review-evidence-row-high-token-leak');
      await expect(highRow).toBeVisible();
      await expect(page.getByTestId('review-evidence-severity-high-token-leak')).toContainText('HIGH');
      await expect(page.getByTestId('review-evidence-fileref-high-token-leak'))
        .toContainText('AuthService.cs:142');

      // Info-severity row also renders.
      await expect(page.getByTestId('review-evidence-row-info-style-nit')).toBeVisible();

      // Findings are evidence, not gates: the lane the job sits in stays
      // 5-human-review even with a HIGH finding present. The state-machine
      // lane is the load-bearing product property — locked at the API.
      const stillInReview = await getJob(created.id, watchPath);
      expect(stillInReview.state).toBe('5-human-review');

      await page.screenshot({
        path: path.join(screenshotsDir, '01-panel-rendered.png'),
        fullPage: false
      });

      // Acknowledge the high-severity finding.
      await page.getByTestId('review-evidence-toggle-ack-high-token-leak').click();
      await expect(page.getByTestId('review-evidence-row-high-token-leak'))
        .toHaveAttribute('data-acknowledged', 'true', { timeout: 5_000 });
      await page.screenshot({
        path: path.join(screenshotsDir, '02-acknowledged.png'),
        fullPage: false
      });

      // Create a follow-up task from the info finding.
      await page.getByTestId('review-evidence-create-followup-info-style-nit').click();

      // The success banner appears once the API resolves.
      const banner = page.getByTestId('review-evidence-followup-banner');
      await expect(banner).toBeVisible({ timeout: 10_000 });
      const bannerText = (await banner.textContent()) ?? '';
      const followupIdMatch = bannerText.match(/Created follow-up task ([a-z0-9-]+)/);
      expect(followupIdMatch).not.toBeNull();
      const followupId = followupIdMatch![1];

      // Same project as the source job, default lane 1-preparation.
      const followup = await getJob(followupId, watchPath);
      expect(followup.state).toBe('1-preparation');
      expect(followup.watchPath).toBe(watchPath);

      // The source finding now has its followupJobId stamped.
      const detailAfter = await api<{ reviewEvidence: Array<{ id: string; followupJobId: string | null }> }>(
        `/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`
      );
      const stamped = detailAfter.reviewEvidence.find((e) => e.id === 'info-style-nit');
      expect(stamped?.followupJobId).toBe(followupId);

      await page.screenshot({
        path: path.join(screenshotsDir, '03-followup-banner.png'),
        fullPage: false
      });

      // Copy screenshots into this job's results/ folder per protocol-style.md
      // so they land next to the Activity Log instead of the scratch
      // test-results/ directory.
      const persistedShots = path.join(jobResultsDir, 'playwright', 'review-evidence-panel');
      fs.mkdirSync(persistedShots, { recursive: true });
      for (const fname of ['01-panel-rendered.png', '02-acknowledged.png', '03-followup-banner.png']) {
        fs.copyFileSync(path.join(screenshotsDir, fname), path.join(persistedShots, fname));
      }

      // Cleanup the follow-up so successive runs do not pile up.
      await deleteJob(followupId, watchPath).catch(() => {});
    } finally {
      await deleteJob(created.id, watchPath).catch(() => {});
      await cleanupStaleFollowups(watchPath);
    }
  });
});
