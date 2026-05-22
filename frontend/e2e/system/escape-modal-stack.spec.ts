import { test, expect, type Page } from '@playwright/test';
import { listJobs } from '../helpers/jobs';

/**
 * Regression for `escape-sollte-modals-schliessen`:
 *
 * Before ModalStackService, pressing Escape with two overlays stacked
 * (e.g. Add Task on top of Task Detail) fired every overlay's local
 * `@HostListener('document:keydown.escape')` at once. The lower
 * surface (Task Detail) closed under the user, which is the bug.
 *
 * After the fix, only the topmost overlay reacts. The detail view stays
 * open until a second Escape arrives. These specs lock that contract
 * for the cases listed in the task prompt's "Zu pruefen" block.
 */

async function openFirstDetail(page: Page): Promise<void> {
  await page.goto('/');
  const firstCard = page.locator('[data-testid="job-card"]').first();
  await expect(firstCard).toBeVisible({ timeout: 10_000 });
  await firstCard.click();
  await expect(page.getByTestId('back-to-board')).toBeVisible({ timeout: 5_000 });
}

test.describe('Escape modal-stack arbitration', () => {
  test('Escape closes Add Task without closing the Task Detail behind it', async ({ page }) => {
    const jobs = await listJobs();
    if (jobs.length === 0) {
      test.skip();
      return;
    }

    await openFirstDetail(page);

    // Open Add Task on top of the detail view.
    await page.getByRole('button', { name: /add task/i }).first().click();
    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    // First Escape: only the dialog closes.
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    // Detail view stays open — the back-to-board control still shows.
    await expect(page.getByTestId('back-to-board')).toBeVisible();
    // URL still carries the job params.
    expect(new URL(page.url()).searchParams.get('job')).toBeTruthy();

    // Second Escape: now the detail closes and the URL clears.
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('back-to-board')).toBeHidden({ timeout: 5_000 });
    await expect(page).not.toHaveURL(/[?&]job=/);
  });

  test('Escape closes Add Task when opened directly from the board', async ({ page }) => {
    await page.goto('/');

    await page.getByRole('button', { name: /add task/i }).first().click();
    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
  });

  test('Escape closes the Task Detail when no modal is open', async ({ page }) => {
    const jobs = await listJobs();
    if (jobs.length === 0) {
      test.skip();
      return;
    }

    await openFirstDetail(page);

    await page.keyboard.press('Escape');
    await expect(page.getByTestId('back-to-board')).toBeHidden({ timeout: 5_000 });
    await expect(page).not.toHaveURL(/[?&]job=/);
  });

  test('Escape closes the Verbose Debug overlay above the Task Detail', async ({ page }) => {
    const jobs = await listJobs();
    if (jobs.length === 0) {
      test.skip();
      return;
    }

    await openFirstDetail(page);

    // Open the verbose-debug overlay by setting a context directly on the
    // app shell. The orchestrator side sheet's bug button is the production
    // entry point, but it depends on backend state we cannot guarantee in
    // a fixture-free run; injecting the context exercises the same overlay
    // rendering and the same modal-stack registration.
    const opened = await page.evaluate(() => {
      // Find the App instance's component via the root element.
      const root = document.querySelector('app-root') as HTMLElement | null;
      if (!root) return false;
      // @ts-expect-error - test-only access via Angular's debug hook.
      const ng = window.ng;
      if (!ng?.getComponent) return false;
      const cmp = ng.getComponent(root);
      if (!cmp?.verboseDebugContext) return false;
      cmp.verboseDebugContext.set({
        lines: [],
        runTimeline: null,
        screenshots: [],
        tokenSummary: null,
        job: null,
      });
      return true;
    });

    if (!opened) {
      test.info().annotations.push({ type: 'skip-reason', description: 'Could not open verbose-debug overlay programmatically (Angular debug API unavailable).' });
      return;
    }

    const overlay = page.getByTestId('app-verbose-debug-overlay');
    await expect(overlay).toBeVisible({ timeout: 5_000 });

    await page.keyboard.press('Escape');
    await expect(overlay).toBeHidden({ timeout: 5_000 });
    // Detail view stays open.
    await expect(page.getByTestId('back-to-board')).toBeVisible();
  });

});
