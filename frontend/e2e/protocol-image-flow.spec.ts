import { test, expect } from '@playwright/test';
import { createJob, getJob, moveJob, listJobs } from './helpers/jobs';
import { api, BACKEND } from './helpers/api';
import * as fs from 'fs';
import * as path from 'path';

interface WatchPath { name: string; path: string; rootPath: string; }

/**
 * Verifies the image flow described in docs/protocol-style.md:
 *
 *   <job>/results/<name>.png  +  status.md `![](results/<name>.png)`
 *     ─►  rendered inline in the protocol pane via /api/jobs/{id}/results/{name}
 *
 * The spec creates a review-state job, drops a tiny PNG into its `results/`
 * folder, hand-writes a status.md that references it, then opens the detail
 * view and asserts the <img> resolved to the API endpoint and actually loaded.
 *
 * Hand-writing status.md is normally forbidden (the SummaryGenerationService
 * owns it and rewrites on each run) — but no run happens here, so the file is
 * stable for the lifetime of the test.
 */

// 1x1 transparent PNG.
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
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

function uid() {
  return `e2e-img-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function cleanupStaleJobs(watchPath: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith('e2e-img-'));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

test.describe('Protocol image flow', () => {
  test('renders results/<name>.png referenced from status.md', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    await cleanupStaleJobs(watchPath);

    const id = uid();
    const created = await createJob({
      id,
      title: `e2e-img-${id}`,
      watchPath,
      targetState: '2-ready'
    });
    await moveJob(created.id, watchPath, '4-review');

    try {
      const job = await getJob(created.id, watchPath);
      const folder = job.folderPath;

      const resultsDir = path.join(folder, 'results');
      fs.mkdirSync(resultsDir, { recursive: true });
      fs.writeFileSync(path.join(resultsDir, 'proof.png'), TINY_PNG);

      const statusMd = [
        '# Status',
        '',
        '- Ergebnis: Erfolg',
        '- Dauer: 1 min',
        '',
        '## Was wurde gemacht',
        '- Test-Screenshot abgelegt.',
        '',
        '## Bilder',
        '- ![proof](results/proof.png)',
        ''
      ].join('\n');
      fs.writeFileSync(path.join(folder, 'status.md'), statusMd);

      // Backend should serve the file we just wrote.
      const directRes = await fetch(
        `${BACKEND}/api/jobs/${encodeURIComponent(created.id)}/results/proof.png?watchPath=${encodeURIComponent(watchPath)}`
      );
      expect(directRes.status).toBe(200);
      expect(directRes.headers.get('content-type')).toContain('image/png');

      await page.goto(
        `/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`
      );

      const protocolTab = page.getByTestId('inspector-tab-protocol');
      await expect(protocolTab).toBeVisible({ timeout: 10_000 });
      await protocolTab.click();

      const body = page.locator('.markdown-preview.notes-panel__body');
      await expect(body).toBeVisible({ timeout: 5_000 });

      const img = body.locator('img').first();
      await expect(img).toBeVisible({ timeout: 5_000 });

      // The resolver must rewrite results/<name> to the API endpoint, and the
      // image must actually decode (naturalWidth > 0) — proving the request
      // hit the backend successfully.
      const src = await img.getAttribute('src');
      expect(src).toContain(`/api/jobs/${created.id}/results/proof.png`);
      expect(src).toContain('watchPath=');

      await expect.poll(
        () => img.evaluate((el: HTMLImageElement) => el.naturalWidth),
        { timeout: 5_000 }
      ).toBeGreaterThan(0);

      await page.screenshot({
        path: 'test-results/protocol-image-flow-rendered.png',
        fullPage: false
      });
    } finally {
      await deleteJob(created.id, watchPath).catch(() => {});
    }
  });
});
