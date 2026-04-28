import { test, expect } from '@playwright/test';
import { listJobs } from './helpers/jobs';
import { api } from './helpers/api';

async function findJobWithOutput(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  let best: { id: string; watchPath: string } | null = null;
  let bestLines = 0;
  for (const j of jobs) {
    try {
      const out = await api<unknown[]>(`/api/jobs/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`);
      if (Array.isArray(out) && out.length > bestLines) {
        bestLines = out.length;
        best = { id: j.id, watchPath: j.watchPath };
      }
    } catch { /* ignore */ }
  }
  return bestLines > 0 ? best : null;
}

interface JobDetailLite {
  info: { id: string; watchPath: string };
  statusMarkdown: string | null;
}

async function findJobWithStatus(): Promise<JobDetailLite | null> {
  for (const j of await listJobs()) {
    try {
      const detail = await api<JobDetailLite>(
        `/api/jobs/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (detail.statusMarkdown && detail.statusMarkdown.length > 10) return detail;
    } catch { /* ignore */ }
  }
  return null;
}

/**
 * Copy buttons for the Task-Log surfaces.
 *
 * Three surfaces gained a "📋 Copy" button:
 *   - Activity log toolbar (parsed / raw / chat) — copies what's visible.
 *   - Protocol section in the maximized log overlay — copies the parsed log.
 *   - Protokoll markdown view in the protocol pane — copies status.md.
 *
 * On http://localhost the async Clipboard API is available in Chromium when
 * we grant the clipboard-write permission. The component falls back to
 * document.execCommand('copy') otherwise; both paths end with the button
 * showing "✓ Copied".
 */
test.describe('Task log — copy buttons', () => {
  test.beforeEach(async ({ context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  });

  test('activity log toolbar exposes Copy and writes raw lines to the clipboard', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByTestId('inspector-tab-activity').click();
    await page.getByTestId('activity-log-mode-raw').click();

    const copyBtn = page.getByTestId('activity-log-copy');
    await expect(copyBtn).toBeVisible({ timeout: 5_000 });
    await expect(copyBtn).toBeEnabled();
    await expect(copyBtn).toContainText('Copy');

    await copyBtn.click();
    await expect(copyBtn).toContainText('Copied', { timeout: 3_000 });

    const clip = await page.evaluate(() => navigator.clipboard.readText());
    expect(clip.length).toBeGreaterThan(0);
    // Raw mode lines start with [<locale-time>] OUT|ERR — the time format
    // depends on the test runner's locale (e.g. "04:14:22 PM" or "16:14:22"),
    // so anchor the assertion on the OUT/ERR stream tag and the bracket prefix.
    expect(clip).toMatch(/\[[^\]]+\]\s+(OUT|ERR)\s/);

    await page.screenshot({ path: 'test-results/activity-log-copy-button.png', fullPage: false });
  });

  test('Protokoll markdown view exposes a Copy button that writes status.md to the clipboard', async ({ page }) => {
    const detail = await findJobWithStatus();
    if (!detail) {
      test.skip(true, 'No job with status.md available');
      return;
    }

    await page.goto(
      `/?job=${encodeURIComponent(detail.info.id)}&watchPath=${encodeURIComponent(detail.info.watchPath)}`
    );

    const protocolTab = page.getByTestId('inspector-tab-protocol');
    await expect(protocolTab).toBeVisible({ timeout: 10_000 });
    await protocolTab.click();

    const copyBtn = page.getByTestId('protocol-copy-markdown');
    await expect(copyBtn).toBeVisible({ timeout: 5_000 });
    await copyBtn.click();
    await expect(copyBtn).toContainText('Copied', { timeout: 3_000 });

    const clip = await page.evaluate(() => navigator.clipboard.readText());
    // The clipboard content must match the markdown the component holds for
    // the job. Trailing whitespace and CRLF differ between disk, JSON, and
    // clipboard round-trips, so normalize before comparing.
    const norm = (s: string) => s.replace(/\r\n/g, '\n').replace(/[ \t]+$/gm, '').trimEnd();
    expect(norm(clip)).toBe(norm(detail.statusMarkdown!));

    await page.screenshot({ path: 'test-results/protocol-copy-button.png', fullPage: false });
  });

  test('maximized log overlay surfaces a Copy button on the Protocol section when entries exist', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByTestId('inspector-tab-activity').click();

    // The "Maximize log" button is only present when there is something to show.
    const maximize = page.getByRole('button', { name: /Maximize log/ });
    if ((await maximize.count()) === 0) {
      test.skip(true, 'No Maximize log button (no activity yet)');
      return;
    }
    await maximize.first().click();

    // The activity-log copy button must also be reachable in the overlay.
    const overlayCopy = page.getByTestId('activity-log-copy');
    await expect(overlayCopy.first()).toBeVisible({ timeout: 5_000 });

    // The protocol-section copy button only renders when the job has parsed
    // protocol entries — older jobs may have none, so treat it as optional.
    const protocolCopy = page.getByTestId('log-overlay-copy-protocol');
    if ((await protocolCopy.count()) > 0) {
      await protocolCopy.click();
      await expect(protocolCopy).toContainText('Copied', { timeout: 3_000 });
    }

    await page.screenshot({ path: 'test-results/log-overlay-copy.png', fullPage: false });
  });
});
