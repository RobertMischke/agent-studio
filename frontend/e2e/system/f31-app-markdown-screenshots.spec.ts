import { test, type Page } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * F31 review-evidence captures: open a job's detail panel and grab the
 * Description and Conversation panes so the reviewer can eyeball the
 * Markdown surfaces after the central `<app-markdown>` migration.
 *
 * The spec is a screenshot-only smoke; it doesn't make assertions about
 * pixel state. It picks the first job from `/api/tasks` and exits cleanly
 * if the workspace has none.
 */

interface ListedJob { id: string; watchPath: string }

const OUT_DIR = 'test-results/f31-app-markdown';

async function pickAnyJob(): Promise<ListedJob | null> {
  const jobs = await api<ListedJob[]>('/api/tasks');
  return jobs[0] ?? null;
}

async function open(page: Page, j: ListedJob): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(j.id)}&watchPath=${encodeURIComponent(j.watchPath)}`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(700);
}

test.describe('F31: <app-markdown> screenshots', () => {
  test('description + activity-log render via <app-markdown>', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs in workspace');
      return;
    }
    await page.setViewportSize({ width: 1600, height: 1000 });
    await open(page, target);

    // Description tab (default). Capture the left pane.
    await page.screenshot({ path: `${OUT_DIR}/01-detail-default.png`, fullPage: false });

    // Activity-log conversation pane on the right.
    const activityTab = page.getByTestId('inspector-tab-activity');
    if (await activityTab.isVisible({ timeout: 2000 }).catch(() => false)) {
      await activityTab.click();
      await page.getByTestId('activity-log-mode-conversation').click({ force: true }).catch(() => {});
      await page.waitForTimeout(500);
      await page.screenshot({ path: `${OUT_DIR}/02-activity-log.png`, fullPage: false });
    }
  });
});
