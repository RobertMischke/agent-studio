import { test, expect } from '@playwright/test';
import { listJobs } from './helpers/jobs';

/**
 * Verifies that pressing F5 (page reload) while viewing a job detail keeps
 * the user on the detail view for the same job rather than returning to the
 * board.
 *
 * Implementation: openDetail() writes ?job=<id>&watchPath=<path> into the
 * URL via history.replaceState, and ngOnInit() reads those params on startup
 * to restore the detail view.
 */

test.describe('Job detail view — F5 / page refresh', () => {
  test('URL contains job params when detail is open', async ({ page }) => {
    const jobs = await listJobs();
    if (jobs.length === 0) {
      test.skip();
      return;
    }

    await page.goto('/');

    // Click the first visible job card on the board.
    const firstCard = page.locator('[data-testid="job-card"]').first();
    await expect(firstCard).toBeVisible({ timeout: 10_000 });
    await firstCard.click();

    // URL should now contain job params.
    await expect(page).toHaveURL(/[?&]job=/, { timeout: 5_000 });
    const url = new URL(page.url());
    expect(url.searchParams.get('job')).toBeTruthy();
    expect(url.searchParams.get('watchPath')).toBeTruthy();
  });

  test('detail view is restored after page reload', async ({ page }) => {
    const jobs = await listJobs();
    if (jobs.length === 0) {
      test.skip();
      return;
    }

    await page.goto('/');

    const firstCard = page.locator('[data-testid="job-card"]').first();
    await expect(firstCard).toBeVisible({ timeout: 10_000 });

    // Capture the job title shown in the card before clicking.
    const jobTitle = await firstCard.locator('[data-testid="job-title"], .job-card__title').first().textContent();

    await firstCard.click();

    // Wait for detail view to appear (back button is the marker).
    const backBtn = page.getByRole('button', { name: /board/i });
    await expect(backBtn).toBeVisible({ timeout: 5_000 });

    // Reload the page (simulate F5).
    await page.reload();

    // After reload the detail view should still be open (not the board).
    await expect(backBtn).toBeVisible({ timeout: 10_000 });

    // The same job title should be visible in the detail view.
    if (jobTitle) {
      await expect(page.getByText(jobTitle.trim(), { exact: false })).toBeVisible({ timeout: 5_000 });
    }
  });

  test('closing detail clears URL params', async ({ page }) => {
    const jobs = await listJobs();
    if (jobs.length === 0) {
      test.skip();
      return;
    }

    await page.goto('/');

    const firstCard = page.locator('[data-testid="job-card"]').first();
    await expect(firstCard).toBeVisible({ timeout: 10_000 });
    await firstCard.click();

    // Wait for URL params to appear.
    await expect(page).toHaveURL(/[?&]job=/, { timeout: 5_000 });

    // Press the back button.
    const backBtn = page.getByRole('button', { name: /board/i });
    await backBtn.click();

    // URL should no longer contain job params.
    await expect(page).not.toHaveURL(/[?&]job=/, { timeout: 5_000 });
  });
});
