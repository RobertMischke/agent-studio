import { test, expect } from '@playwright/test';

test('open the failed screenshots-in-editors task and capture errors', async ({ page }) => {
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') consoleErrors.push(msg.text());
  });
  page.on('pageerror', (err) => pageErrors.push(err.stack ?? err.message));

  await page.goto('/?job=screenshots-in-editors&watchPath=' + encodeURIComponent('C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard'));
  await page.waitForTimeout(2500);

  // The failure modal pops once on entry — dismiss it via the ✕ button.
  const errorHeading = page.locator('.error-dialog__title');
  if (await errorHeading.isVisible().catch(() => false)) {
    await page.locator('.error-dialog__close').click();
    await expect(errorHeading).toHaveCount(0, { timeout: 2000 });
  }

  // Wait through 2-3 board-refresh ticks (2 s each) — the modal must not re-pop.
  await page.waitForTimeout(7000);

  await page.screenshot({ path: 'open-failed-task.png', fullPage: true });

  // After dismissal, the dialog must stay closed even though the backend keeps
  // re-emitting the same failed CliExecution snapshot on every refresh.
  await expect(page.locator('.error-dialog__title')).toHaveCount(0);

  console.log('CONSOLE_ERRORS:', JSON.stringify(consoleErrors, null, 2));
  console.log('PAGE_ERRORS:', JSON.stringify(pageErrors, null, 2));
  expect(pageErrors).toEqual([]);
});
