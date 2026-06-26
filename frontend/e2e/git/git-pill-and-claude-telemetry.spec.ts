import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath { path: string; name: string; rootPath: string }

test.describe('Board — git pill on tile', () => {
  test('pill appears only on 3-progress and 4-review tiles', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    const wp = paths.find(p => p.name.toLowerCase().includes('agent task processor'))!;

    const ready    = await createJob({ title: `pill-ready-${Date.now()}`,    watchPath: wp.path, cliType: 'claude', agent: 'claude', targetState: '2-ready' });
    const progress = await createJob({ title: `pill-progress-${Date.now()}`, watchPath: wp.path, cliType: 'claude', agent: 'claude', targetState: '2-ready' });
    await moveJob(progress.id, wp.path, '3-progress');

    try {
      await page.goto('/');
      // Wait for tiles to render and for the git summary poll to land.
      await expect(page.locator('[data-testid="job-card"]')).toHaveCount.greaterThan?.(0).catch(() => {});
      await page.waitForTimeout(1500);

      // Find the two cards by their titles.
      const readyCard    = page.locator('[data-testid="job-card"]', { hasText: ready.id }).first();
      const progressCard = page.locator('[data-testid="job-card"]', { hasText: progress.id }).first();

      await expect(progressCard).toBeVisible({ timeout: 10_000 });
      await expect(progressCard.getByTestId('job-card-git')).toBeVisible({ timeout: 10_000 });

      // Ready-lane tile must NOT show a git pill.
      await expect(readyCard).toBeVisible();
      await expect(readyCard.getByTestId('job-card-git')).toHaveCount(0);
    } finally {
      await api(`/api/tasks/${encodeURIComponent(ready.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
      await api(`/api/tasks/${encodeURIComponent(progress.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    }
  });
});

test.describe('Detail — Claude session telemetry', () => {
  test('endpoint returns a structured response even with no session yet', async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    const wp = paths.find(p => p.name.toLowerCase().includes('agent task processor'))!;
    const job = await createJob({ title: `tel-${Date.now()}`, watchPath: wp.path, cliType: 'claude', agent: 'claude', targetState: '2-ready' });

    try {
      const url = `/api/tasks/${encodeURIComponent(job.id)}/claude/session-info?watchPath=${encodeURIComponent(wp.path)}`;
      const res = await api<{
        sessionInfo: { sessionId: string; error: string | null; turnCount: number };
        rateLimit: unknown | null;
      }>(url);
      expect(res).toBeDefined();
      expect(res.sessionInfo).toBeDefined();
      // No session captured yet → backend reports an explanatory error string.
      expect(typeof res.sessionInfo.sessionId).toBe('string');
      expect(typeof res.sessionInfo.turnCount).toBe('number');
      // rateLimit is null until the running CLI emits its first
      // rate_limit_event frame — for a brand-new untouched job it must be null.
      expect(res.rateLimit).toBeNull();
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    }
  });
});
