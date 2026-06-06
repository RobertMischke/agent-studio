import { test, expect, type Page } from '@playwright/test';

/**
 * Visual evidence for the Move/Undo toast position fix.
 *
 * The Move/Undo action toast now docks BOTTOM-RIGHT (position
 * 'bottom-right') so it no longer covers the task-detail context menu
 * that opens in the TOP-RIGHT corner. All other toasts keep their
 * default top-right position.
 *
 * Drives the live NotificationService via the `window.__notifications`
 * hook so it needs no backend data: one default top-right toast and one
 * bottom-right Move/Undo toast, captured side by side.
 */

const RESULTS_DIR = process.env.EVIDENCE_RESULTS_DIR ?? '';

async function waitForNotificationService(page: Page): Promise<void> {
  await page.waitForFunction(() => Boolean((window as { __notifications?: unknown }).__notifications));
}

test('Move/Undo toast docks bottom-right; top-right corner stays free', async ({ page }, testInfo) => {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await waitForNotificationService(page);

  // Backendless dev serve pops a "Failed to load runner status" error
  // modal; close it so the evidence shows only the toast surfaces.
  const errClose = page.locator('[data-testid="error-dialog-close"]');
  for (let i = 0; i < 3; i++) {
    if (await errClose.first().isVisible().catch(() => false)) {
      await errClose.first().click().catch(() => {});
      await page.waitForTimeout(150);
    }
  }

  // Dismiss any incidental toasts (e.g. backend-unreachable errors) so the
  // evidence shows only the two we care about.
  await page.evaluate(() => {
    (window as unknown as { __notifications: { dismissAll: () => void } }).__notifications.dismissAll();
  });

  await page.evaluate(() => {
    const svc = (window as unknown as {
      __notifications: { notify: (o: Record<string, unknown>) => number };
    }).__notifications;
    // Default position → top-right pile (where the context menu also opens).
    svc.notify({ kind: 'info', title: 'Update available', message: 'A new runner build is ready.', durationMs: 0 });
    // Move/Undo action toast → bottom-right pile (the fix).
    svc.notify({
      kind: 'info',
      message: 'Moved "Fix lane badge" → Completed',
      position: 'bottom-right',
      actions: [{ label: 'Undo', testId: 'undo-action', primary: true, callback: () => { /* evidence */ } }],
    });
  });

  const topStack = page.getByTestId('notification-stack');
  const bottomStack = page.getByTestId('notification-stack-bottom-right');
  await expect(topStack.getByTestId('notification-message').first()).toBeVisible({ timeout: 5_000 });
  await expect(bottomStack.getByTestId('undo-action')).toBeVisible({ timeout: 5_000 });

  // Geometry assertion: the undo toast sits in the lower half; the
  // top-right pile sits in the upper half. This is what frees the
  // top-right context menu.
  const vh = page.viewportSize()!.height;
  const undoBox = await bottomStack.getByTestId('undo-action').boundingBox();
  const topBox = await topStack.getByTestId('notification-message').first().boundingBox();
  expect(undoBox!.y).toBeGreaterThan(vh / 2);
  expect(topBox!.y).toBeLessThan(vh / 2);

  await page.waitForTimeout(300);
  const buf = await page.screenshot({ fullPage: false });
  await testInfo.attach('undo-toast-bottom-right.png', { body: buf, contentType: 'image/png' });
  if (RESULTS_DIR) {
    await page.screenshot({ path: `${RESULTS_DIR}/undo-toast-bottom-right.png`, fullPage: false });
  }
});
