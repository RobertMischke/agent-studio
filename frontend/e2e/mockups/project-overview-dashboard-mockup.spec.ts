import { expect, test } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

/** Interactive design reference for the operator-first Project Overview. */

const RESULTS_DIR = process.env.PROJECT_OVERVIEW_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-overview-dashboard-mockup');
const MOCKUP_URL = pathToFileURL(path.resolve(__dirname, '..', '..', '..', 'docs', 'mockups', 'project-overview-dashboard', 'ui.html')).href;

test.describe('Project Overview interactive mockup', () => {
  test.beforeAll(() => fs.mkdirSync(RESULTS_DIR, { recursive: true }));

  test('covers both themes, URL start, evidence review, and deployment drilldown', async ({ page }) => {
    await page.setViewportSize({ width: 1536, height: 980 });
    await page.goto(MOCKUP_URL);
    await expect(page.locator('#overview')).toBeVisible();
    await expect(page.locator('#completed7')).toHaveText('29');
    await expect(page.locator('#evidenceCount')).toHaveText('3');
    await expect(page.locator('#runningUrlCount')).toHaveText('2');
    await expect(page.locator('#deployDelta')).toHaveText('5');

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'project-overview-mockup--light--mocked.png'),
      fullPage: true,
    });
    await page.locator('#themeToggle').click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await page.screenshot({
      path: path.join(RESULTS_DIR, 'project-overview-mockup--dark--mocked.png'),
      fullPage: true,
    });

    await page.locator('[data-start-url="url-storybook"]').first().click();
    await expect(page.locator('#runningUrlCount')).toHaveText('3');

    await page.locator('[data-detail="evidence"]').last().click();
    await expect(page.locator('#detailSheet')).toBeVisible();
    await page.locator('[data-review-evidence]').click();
    await expect(page.locator('#evidenceCount')).toHaveText('2');
    await page.locator('#closeSheet').click();

    await page.locator('[data-detail="deployment"]').click();
    await expect(page.locator('#detailBody')).toContainText('AGT-2097 document deployment as a product object');
    await expect(page.locator('#detailBody').locator('li')).toHaveCount(5);
  });
});
