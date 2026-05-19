import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Baseline capture for the job-detail / app refactor.
 *
 * Runs through the visual states we care about and writes screenshots into
 * frontend/e2e/_baselines/. A second pass (after the refactor) writes into
 * _baselines-after/ so we can diff manually.
 *
 * Toggle the output folder via REFACTOR_BASELINE_PASS=before|after.
 */
const PASS = process.env.REFACTOR_BASELINE_PASS ?? 'before';
// After the e2e reorg the spec lives in `dev-tools/`; the baselines
// directory still sits at `e2e/_baselines{,-after}/`, so we walk one
// level up.
const OUT_DIR = path.resolve(__dirname, '..', PASS === 'after' ? '_baselines-after' : '_baselines');

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

function shot(name: string) {
  return path.join(OUT_DIR, `${name}.png`);
}

test.beforeAll(() => {
  fs.mkdirSync(OUT_DIR, { recursive: true });
});

test.describe('@baseline refactor visual capture', () => {
  test('board view', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    // Wait until at least one column is rendered.
    await expect(page.locator('[data-testid="job-column"], .column, .board__column').first()).toBeVisible({ timeout: 10_000 }).catch(() => {});
    await page.screenshot({ path: shot('01-board'), fullPage: true });
  });

  test('detail view — default 2 panes', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `baseline-default-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Baseline\n\nSome **markdown** content.',
      targetState: '2-ready'
    });
    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-prompt')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('pane-protocol')).toBeVisible();
      await page.waitForTimeout(500);
      await page.screenshot({ path: shot('02-detail-default'), fullPage: true });
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('detail view — all three panes + command deck', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `baseline-3pane-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Three panes',
      targetState: '2-ready'
    });
    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-prompt')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();
      await page.waitForTimeout(800);
      await page.screenshot({ path: shot('03-detail-three-panes'), fullPage: true });

      // Capture command deck closer crop
      const deck = page.locator('.sidebar-card--toolbar').first();
      if (await deck.isVisible().catch(() => false)) {
        await deck.screenshot({ path: shot('04-command-deck') });
      }
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('detail view — protocol pane maximized', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `baseline-max-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Max',
      targetState: '2-ready'
    });
    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      const max = page.getByTestId('pane-maximize-protocol');
      if (await max.isVisible().catch(() => false)) {
        await max.click();
        await page.waitForTimeout(400);
        await page.screenshot({ path: shot('05-protocol-maximized'), fullPage: true });
      }
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
