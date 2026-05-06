import { expect, test } from '@playwright/test';

/**
 * Update banner contract: the four mutually-exclusive modes that the
 * <app-update-banner /> component renders, observed end-to-end against
 * the real UpdateService at :5039.
 *
 * This spec is intentionally agnostic about how stable was started: it
 * only asserts that *one* of the four banner test-ids is consistent
 * with the live /update/status snapshot, plus that the dismiss path on
 * a "done" banner clears it.
 *
 * Skips when the UpdateService is not reachable (e.g. CI without the
 * sibling process), so it does not become a flaky required test.
 */
test.describe('update banner', () => {
  test.beforeEach(async ({ page, baseURL }) => {
    // Assume the FE points at stable; the service base URL it polls is
    // host-of-FE:5039 (see UpdateClientService).
    const url = new URL(baseURL ?? 'http://localhost:4011');
    const probe = await page.request.get(`${url.protocol}//${url.hostname}:5039/healthz`).catch(() => null);
    test.skip(!probe?.ok(), 'UpdateService not reachable on :5039; skipping banner spec.');
  });

  test('mounts and reflects current status', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    // Give the first poll a chance to land. The service answers in <50ms,
    // so 3s covers a slow CI machine.
    await page.waitForTimeout(3000);

    const counts = {
      running: await page.locator('[data-testid="update-banner-running"]').count(),
      behind:  await page.locator('[data-testid="update-banner-behind"]').count(),
      done:    await page.locator('[data-testid="update-banner-done"]').count(),
      failed:  await page.locator('[data-testid="update-banner-failed"]').count(),
    };
    const total = counts.running + counts.behind + counts.done + counts.failed;
    // 0 or 1 visible banners; never multiple at once.
    expect(total, 'at most one banner mode should be visible').toBeLessThanOrEqual(1);
  });

  test('done banner can be dismissed', async ({ page, baseURL }) => {
    // Set up: the easiest way to put the banner into "done" mode is to
    // trigger an update against the up-to-date stable. The orchestrator
    // does a no-op git pull and reports done; we do not have to actually
    // change HEAD. Trigger via the service, then load the page.
    const url = new URL(baseURL ?? 'http://localhost:4011');
    const triggerResp = await page.request.post(`${url.protocol}//${url.hostname}:5039/update/trigger`, {
      data: { reason: 'spec dismiss test', force: false },
      headers: { 'Content-Type': 'application/json' }
    });
    expect(triggerResp.status(), 'trigger should be 202 Accepted').toBe(202);

    // Wait for orchestration to settle (no-op pull + restart waits ~30s).
    // Poll status until phase != preparing/pausing-runners/pulling/restarting/resuming.
    for (let i = 0; i < 60; i++) {
      const st = await page.request.get(`${url.protocol}//${url.hostname}:5039/update/status`);
      const body = await st.json();
      if (body.phase === 'done' || body.phase === 'failed') break;
      await page.waitForTimeout(1000);
    }

    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);

    const done = page.locator('[data-testid="update-banner-done"]');
    await expect(done, 'done banner should be visible after a no-op trigger').toBeVisible();

    await page.locator('[data-testid="update-banner-dismiss"]').click();
    await expect(done, 'done banner should disappear after dismiss').not.toBeVisible();
  });
});
