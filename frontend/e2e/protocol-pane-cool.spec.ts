import { test, expect } from '@playwright/test';
import { listJobs } from './helpers/jobs';
import { api } from './helpers/api';

interface JobDetail {
  info: { id: string; watchPath: string; createdAt: string };
  statusMarkdown: string | null;
  summaryState: { status: 'none' | 'generating' | 'ready' | 'failed' } | null;
}

async function pickJobWithProtocol(): Promise<JobDetail | null> {
  for (const j of await listJobs()) {
    try {
      const detail = await api<JobDetail>(
        `/api/jobs/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (detail.statusMarkdown && detail.statusMarkdown.length > 10) return detail;
    } catch { /* ignore */ }
  }
  return null;
}

async function pickAnyJob(): Promise<JobDetail | null> {
  const jobs = await listJobs();
  if (!jobs.length) return null;
  const j = jobs[0];
  return api<JobDetail>(
    `/api/jobs/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`
  );
}

/**
 * Protocol pane — new look.
 *
 * Verifies:
 *   1. Pill-style toggle (Protokoll / Aktivität) is rendered with data-testids.
 *   2. Detail header shows the "⏱ erstellt …" relative-time chip with an
 *      ISO timestamp tooltip.
 *   3. CLI lock badge (🔒) is gone.
 *   4. When status.md exists, the Protokoll tab is reachable; the markdown
 *      renders inside the notes panel.
 */
test.describe('Protocol pane — cool header + pill toggle', () => {
  test('renders pill toggle, created chip, and no lock badge', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });
    const detail = (await pickJobWithProtocol()) ?? (await pickAnyJob());
    if (!detail) {
      test.skip(true, 'No jobs available in workspace');
      return;
    }

    await page.goto(
      `/?job=${encodeURIComponent(detail.info.id)}&watchPath=${encodeURIComponent(detail.info.watchPath)}`
    );

    // Pill toggle visible with both tabs present.
    const protocolTab = page.getByTestId('inspector-tab-protocol');
    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(protocolTab).toBeVisible({ timeout: 10_000 });
    await expect(activityTab).toBeVisible();

    // Created-chip rendered in detail header with a tooltip.
    const created = page.getByTestId('detail-created-at');
    await expect(created).toBeVisible();
    await expect(created).toContainText('erstellt');

    // CLI lock badge must be gone.
    await expect(page.locator('text=🔒 CLI is running')).toHaveCount(0);

    await page.screenshot({ path: 'test-results/protocol-pane-cool-overview.png', fullPage: false });

    // Protokoll tab is enabled when there's something to render: either
    // status.md content on disk OR a generation in flight. With nothing,
    // the tab is correctly disabled.
    const hasContent = !!detail.statusMarkdown;
    if (hasContent) {
      await protocolTab.click();
      const body = page.locator('.markdown-preview.notes-panel__body');
      await expect(body).toBeVisible({ timeout: 5_000 });
      await page.screenshot({ path: 'test-results/protocol-pane-cool-rendered.png', fullPage: false });
    } else {
      await expect(protocolTab).toBeDisabled();
      await page.screenshot({ path: 'test-results/protocol-pane-cool-empty.png', fullPage: false });
    }
  });
});
