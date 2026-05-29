import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, waitForJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/**
 * F38 acceptance: prompt-pane and protocol-pane both expose their
 * sub-views via a single tab strip projected into `<app-pane-header>`'s
 * `[tabs]` slot. The two headers must share the same row geometry,
 * active-state styling, and BEM class vocabulary so they read as one
 * consistent surface.
 *
 * Uses the real backend (per repo convention) so route stubbing does
 * not race the page bootstrap.
 */

const SCREENSHOT_DIR = 'test-results';

test.describe('F38: unified pane-header tab strip across prompt + protocol', () => {
  test.beforeEach(async () => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
  });

  test('both panes expose tabs from the pane-header [tabs] slot', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f38-unified-headers-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# F38 pane-header-unified fixture',
      targetState: '2-ready',
    });

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const promptHeader = page.getByTestId('pane-prompt-header');
      const protocolHeader = page.getByTestId('pane-protocol-header');
      await expect(promptHeader).toBeVisible({ timeout: 15_000 });
      await expect(protocolHeader).toBeVisible({ timeout: 15_000 });

      // Every documented tab lives inside its own pane-header.
      for (const id of ['prompt-tab-description', 'prompt-tab-evidence', 'prompt-tab-code-review']) {
        await expect(promptHeader.getByTestId(id)).toBeVisible();
      }
      for (const id of ['inspector-tab-protocol', 'inspector-tab-activity']) {
        await expect(protocolHeader.getByTestId(id)).toBeVisible();
      }

      // The same row carries the maximize + hide buttons that
      // <app-pane-header> renders at the trailing edge.
      await expect(promptHeader.getByTestId('pane-header-maximize')).toBeVisible();
      await expect(protocolHeader.getByTestId('pane-header-maximize')).toBeVisible();
      await expect(promptHeader.getByTestId('pane-header-hide')).toBeVisible();
      await expect(protocolHeader.getByTestId('pane-header-hide')).toBeVisible();

      // Visual evidence dedup — the legacy strip is gone from the
      // protocol-pane regardless of layout state.
      await expect(page.getByTestId('protocol-screenshot-strip')).toHaveCount(0);

      await page.screenshot({
        path: join(SCREENSHOT_DIR, 'f38-pane-header-unified.png'),
        fullPage: false,
      });
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('prompt + protocol headers share row height (single-row pattern)', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f38-header-row-height-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# F38 row-height fixture',
      targetState: '2-ready',
    });

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const prompt = page.getByTestId('pane-prompt-header');
      const protocol = page.getByTestId('pane-protocol-header');
      await expect(prompt).toBeVisible({ timeout: 15_000 });
      await expect(protocol).toBeVisible({ timeout: 15_000 });

      const [promptBox, protocolBox] = await Promise.all([prompt.boundingBox(), protocol.boundingBox()]);
      expect(promptBox).not.toBeNull();
      expect(protocolBox).not.toBeNull();
      // Allow a 4px slack to absorb sub-pixel rendering differences.
      expect(Math.abs((promptBox!.height ?? 0) - (protocolBox!.height ?? 0))).toBeLessThanOrEqual(4);
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('active tabs share the same active-state class vocabulary', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f38-active-class-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# F38 active-class fixture',
      targetState: '2-ready',
    });

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      // Race-proof the deep-link: wait for the scanner to surface the
      // fixture before navigating, otherwise the URL restore loses the
      // race against the JobIndexCache and the welcome screen wins.
      await waitForJob(job.id, watchPath, () => true, { timeoutMs: 15_000 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      // The prompt pane defaults to Overview on every task switch; click
      // into Description to assert the canonical active-class transfers
      // when the user picks a tab.
      const description = page.getByTestId('prompt-tab-description');
      await expect(description).toBeVisible({ timeout: 15_000 });
      await description.click();
      await expect(description).toHaveClass(/pane-tab--active/);

      // Click into Activity on the right; the same class applies to the
      // tab the protocol header exposes.
      const activity = page.getByTestId('inspector-tab-activity');
      await activity.click();
      await expect(activity).toHaveClass(/pane-tab--active/);
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
