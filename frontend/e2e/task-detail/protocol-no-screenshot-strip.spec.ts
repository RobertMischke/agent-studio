import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/**
 * F38 dedup: visual evidence (screenshot strip) lives only in the
 * prompt-pane's Evidence tab. The protocol pane must NOT render a
 * duplicate `<app-screenshot-strip>` regardless of layout, and the
 * legacy `protocol-screenshot-strip` testid is gone from the codebase.
 */

test.describe('F38: protocol pane no longer renders the screenshot strip', () => {
  test('no protocol-screenshot-strip in DOM and no <app-screenshot-strip> in protocol pane', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f38-no-strip-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# F38 no-strip fixture',
      targetState: '2-ready',
    });

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const protocol = page.getByTestId('pane-protocol');
      await expect(protocol).toBeVisible({ timeout: 15_000 });

      // The legacy testid is gone everywhere; the canonical
      // `screenshot-strip` testid must not appear inside the
      // protocol pane (it stays available inside the prompt-pane
      // Evidence tab when there are screenshots).
      await expect(protocol.getByTestId('protocol-screenshot-strip')).toHaveCount(0);
      await expect(protocol.getByTestId('screenshot-strip')).toHaveCount(0);
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
