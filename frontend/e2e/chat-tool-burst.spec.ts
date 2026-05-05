import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

const FLAG_KEY = 'atp.flag.nextGenChatPrototype';

/**
 * Visual coverage for the next-gen chat tool-burst chip
 * (`Frontend:NextGenChat`). The chip lives inside the next-gen chat
 * prototype host: in production it will render `ToolBurstEvent`s emitted
 * by `projectConversation`; here we drive it through the prototype's
 * static fixture so the row can be exercised end-to-end without a
 * backend roundtrip.
 *
 * Screenshots land under the running task's `results/` folder so the
 * review surface stays close to the Activity Log; `test-results/` is
 * scratch and gets overwritten.
 */
const RESULTS_DIR = path.resolve(
  __dirname,
  '../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/chat-tool-burst-collapsing/results'
);

async function enablePrototype(page: Page): Promise<void> {
  await page.addInitScript((key) => {
    localStorage.setItem(key, '1');
  }, FLAG_KEY);
}

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = route.request().url();
    if (url.includes('/watch-paths')) return route.fulfill({ json: [] });
    if (url.includes('/jobs/grouped')) {
      return route.fulfill({
        json: {
          preparation: [], ready: [], progress: [], review: [],
          autoReview: [], humanReview: [], completed: [], archive: []
        }
      });
    }
    if (url.includes('/runner/status')) return route.fulfill({ json: { projects: {} } });
    if (url.includes('/cli/quota')) return route.fulfill({ json: { snapshots: [] } });
    if (url.includes('/cli/usage')) return route.fulfill({ json: { sessions: [], versions: [] } });
    return route.fulfill({ json: [] });
  });
}

test.describe('@mockup next-gen chat tool-burst chip', () => {
  test('collapses contiguous tool activity into one dense row with failure visible', async ({ page }) => {
    await stubApi(page);
    await enablePrototype(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.addStyleTag({
      content: '.dev-banner{display:none!important}body{padding-top:0!important}'
    });

    const chip = page.getByTestId('tool-burst-chip');
    await expect(chip).toBeVisible();
    // Acceptance: tool-heavy fixtures collapse into a single row, not a wall
    // of chips. The total stays prominent and the failure count never hides.
    await expect(page.getByTestId('tool-burst-total')).toContainText('Tools 28');
    await expect(page.getByTestId('tool-burst-failures')).toContainText('1 failed');
    const families = page.getByTestId('tool-burst-families');
    await expect(families).toContainText('read 12');
    await expect(families).toContainText('search 7');
    await expect(families).toContainText('edit 4');

    await page.screenshot({
      path: path.join(RESULTS_DIR, '01-tool-burst-collapsed-comfortable.png'),
      fullPage: false
    });

    // Expand: the per-tool table appears with the family rollup, tests, and
    // artifacts. The raw range is shown so Trace stays one click away.
    await page.getByTestId('tool-burst-row').click();
    const details = page.getByTestId('tool-burst-details');
    await expect(details).toBeVisible();
    await expect(page.getByTestId('tool-burst-table')).toBeVisible();
    await expect(page.getByTestId('tool-burst-range')).toContainText('cli-output.log:1-180');
    await expect(page.getByTestId('tool-burst-tests')).toContainText('npx playwright test');
    await expect(page.getByTestId('tool-burst-artifacts-list')).toContainText('.png');

    await page.screenshot({
      path: path.join(RESULTS_DIR, '02-tool-burst-expanded.png'),
      fullPage: false
    });

    // Compact density still keeps the failure count in view; secondary chips
    // (duration, files) hide first to preserve task height.
    await page.getByTestId('tool-burst-row').click();
    await page.getByTestId('prototype-density-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype'))
      .toHaveAttribute('data-density', 'compact');
    await expect(page.getByTestId('tool-burst-failures')).toBeVisible();
    await page.screenshot({
      path: path.join(RESULTS_DIR, '03-tool-burst-compact.png'),
      fullPage: false
    });

    // Mobile: the row stays under the viewport width and the failure count
    // continues to be visible. Family chips collapse aggressively.
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByTestId('tool-burst-chip')).toBeVisible();
    await expect(page.getByTestId('tool-burst-failures')).toBeVisible();
    await page.screenshot({
      path: path.join(RESULTS_DIR, '04-tool-burst-mobile.png'),
      fullPage: false
    });
  });
});
