import { test } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name: string; rootPath: string }

/**
 * Visual capture of the new session-status chip in three states (continued /
 * lost / fresh). Not a regression spec — meant for the chat reply where the
 * user explicitly asks to see UI changes. The session-events log is seeded
 * directly via the filesystem so we don't need to spin up a CLI.
 */
test('session chip — visual capture (continued / lost / fresh)', async ({ page }) => {
  test.skip(!process.env.CAPTURE_SCREENSHOTS, 'opt-in via CAPTURE_SCREENSHOTS=1');
  await page.setViewportSize({ width: 1800, height: 900 });

  const paths = await api<WatchPath[]>('/api/watch-paths');
  const wp = paths.find(p => p.name.toLowerCase().includes('agent task processor'))!;
  const job = await createJob({
    title: `chip-shot-${Date.now()}`,
    watchPath: wp.path,
    cliType: 'claude',
    agent: 'claude',
    targetState: '2-ready'
  });

  try {
    await fetch(`http://127.0.0.1:5030/api/jobs/${encodeURIComponent(job.id)}/continue?watchPath=${encodeURIComponent(wp.path)}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ prompt: 'noop' })
    });
    await api(
      `/api/jobs/${encodeURIComponent(job.id)}/stop?watchPath=${encodeURIComponent(wp.path)}`,
      { method: 'POST' }
    ).catch(() => {});

    await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(wp.path)}`);
    const chip = page.getByTestId('session-chip-lost');
    await chip.waitFor({ state: 'visible', timeout: 15_000 });
    // Capture the full protocol-pane header so the chip is visible in
    // context next to the existing telemetry chips.
    const header = page.locator('[data-testid="pane-protocol"] .pane__header').first();
    await header.screenshot({ path: 'test-results/session-chip-lost.png' });
  } finally {
    await api(
      `/api/jobs/${encodeURIComponent(job.id)}/stop?watchPath=${encodeURIComponent(wp.path)}`,
      { method: 'POST' }
    ).catch(() => {});
    await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
  }
});
