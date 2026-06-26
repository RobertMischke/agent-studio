import { test } from '@playwright/test';
import * as path from 'path';
import { api } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await api(
    `/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' },
  ).catch(() => {});
}

async function cleanup(prefix: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath)));
}

/**
 * One-off screenshot capture for the kanban search toolbar. Drops three
 * PNGs under test-results/ (empty, filtered, cleared) so the agent can
 * paste them into the review chat. Not a regression spec; runs alongside
 * board-search.spec.ts purely as evidence.
 */
test('board search — empty + filtered states', async ({ page }) => {
  const PREFIX = 'e2e-search-shot-';
  await cleanup(PREFIX);
  const paths = await api<WatchPath[]>('/api/watch-paths');
  test.skip(!paths.length, 'no watch paths configured');
  const target = paths.find(p => /agent-taskboard/i.test(p.path)) ?? paths[0];
  const watchPath = target.path;

  const uniqueA = `zorblax${Date.now().toString(36)}`;
  const a = await createJob({
    id: PREFIX + uniqueA, title: `Card A about ${uniqueA}`, watchPath,
    cliType: 'claude', agent: 'claude', promptMarkdown: 'p',
    targetState: '1-preparation', fixture: false,
  });

  await page.addInitScript(() => {
    localStorage.setItem('activeProjects', '[]');
  });

  try {
    await page.goto('/');
    await page.waitForTimeout(800);
    // 1. Idle state: bare search icon in the header.
    await page.screenshot({ path: path.join('test-results', 'board-search-empty.png'), fullPage: false });

    // 2. Expanded state with an active query.
    await page.getByTestId('board-search-icon').click();
    await page.getByTestId('board-search-input').fill(uniqueA);
    await page.waitForTimeout(250);
    await page.screenshot({ path: path.join('test-results', 'board-search-filtered.png'), fullPage: false });

    // 3. Esc collapses to the slim chip while the query stays active.
    await page.getByTestId('board-search-input').press('Escape');
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join('test-results', 'board-search-chip.png'), fullPage: false });

    // 4. Chip × clears the query and the bare icon returns.
    await page.getByTestId('board-search-chip-clear').click();
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join('test-results', 'board-search-cleared.png'), fullPage: false });
  } finally {
    await deleteJob(a.id, watchPath);
  }
});
