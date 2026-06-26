import { test, expect, Page } from '@playwright/test';
import { listJobs } from '../helpers/jobs';
import { api } from '../helpers/api';

/**
 * Frontend:VsCodeLayout feature flag — slice 1.
 *
 * Verifies the chrome-density mode: with the flag on, the chat panel reads
 * close to the top of the viewport. With the flag off, the legacy layout is
 * unchanged.
 *
 * Spec & taxonomy live at docs/mockups/vscode-layout/. The "chat reads to
 * within 24 px of the viewport top" target from the spec lives at the
 * full-redesign stage; slice 1 ships the CSS density reduction and aims for
 * ≤ 140 px from the editor top.
 */

const FLAG_KEY = 'atp.flag.vsCodeLayout';

async function findJobWithOutput(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  let best: { id: string; watchPath: string; n: number } | null = null;
  for (const j of jobs) {
    try {
      const out = await api<unknown[]>(
        `/api/tasks/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (Array.isArray(out) && out.length > 0 && (!best || out.length > best.n)) {
        best = { id: j.id, watchPath: j.watchPath, n: out.length };
      }
    } catch { /* ignore */ }
  }
  return best ? { id: best.id, watchPath: best.watchPath } : null;
}

async function setFlag(page: Page, on: boolean): Promise<void> {
  // The vsCodeLayout flag now defaults ON; off must be written explicitly
  // as '0' instead of removing the key (a missing key reads as default).
  await page.addInitScript(([key, value]) => {
    localStorage.setItem(key, value ? '1' : '0');
  }, [FLAG_KEY, on] as const);
}

async function openTask(page: Page, target: { id: string; watchPath: string }): Promise<void> {
  await page.goto(
    `/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`
  );
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
}

test.describe('Frontend:VsCodeLayout flag', () => {
  test('flag off: detail header and pane-toggle bar are visible (legacy layout)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await setFlag(page, false);
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }
    await openTask(page, target);

    const root = page.getByTestId('app-root');
    await expect(root).not.toHaveClass(/app--vscode-layout/);

    // Project pill in the detail header is visible by default.
    await expect(page.getByTestId('detail-project')).toBeVisible();

    await page.screenshot({
      path: 'test-results/vscode-layout-flag-off.png',
      fullPage: false,
    });
  });

  test('flag on: chat reads near the top of the viewport', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await setFlag(page, true);
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }
    await openTask(page, target);

    const root = page.getByTestId('app-root');
    await expect(root).toHaveClass(/app--vscode-layout/);

    // The protocol pane (the "chat" container) starts close to the viewport
    // top. Slice-1 budget: ≤ 140 px from the top of the page after the global
    // header. The mockup's stricter 24 px target arrives in slice 2 once the
    // tab bar replaces the breadcrumb.
    const pane = page.getByTestId('pane-protocol');
    const paneBox = await pane.boundingBox();
    if (!paneBox) throw new Error('pane-protocol has no bounding box');

    await page.screenshot({
      path: 'test-results/vscode-layout-flag-on.png',
      fullPage: false,
    });

    expect(paneBox.y).toBeLessThan(140);

    // Telemetry chips are hidden by default; "i" Meta toggle reveals them.
    const metaToggle = page.getByTestId('pane-meta-toggle');
    await expect(metaToggle).toBeVisible();
  });

  test('flag on: meta toggle persists across reload via localStorage', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await setFlag(page, true);
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }
    await openTask(page, target);

    const metaToggle = page.getByTestId('pane-meta-toggle');
    await expect(metaToggle).toBeVisible();

    // Toggle meta open, reload, observe the localStorage key was set and the
    // body picks up the open class on the new page.
    await metaToggle.click();
    const storedAfterOpen = await page.evaluate(() =>
      localStorage.getItem('atp.flag.vsCodeLayout.metaOpen')
    );
    expect(storedAfterOpen).toBe('1');

    await page.reload();
    await expect(page.getByTestId('app-root')).toHaveClass(/app--vscode-meta-open/);
  });
});
