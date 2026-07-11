import { expect, test } from '@playwright/test';
import path from 'node:path';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], review: [], autoReview: [], humanReview: [], escalated: [],
  codeNotComplete: [], completed: [], archive: [],
};

test('restored task tab hides its watch path and centres its key', async ({ page }) => {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const body = url.includes('/api/tasks/grouped') ? EMPTY_GROUPED
      : url.includes('/api/runner/status') ? { projects: {} }
      : [];
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.addInitScript(() => {
    const taskKey = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard::ASS-1766';
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'hub', projectName: 'Agent Software Studio', section: 'wiki' },
        { kind: 'task', taskKey },
      ],
      activeKey: `task:${taskKey}`,
    }));
  });

  await page.goto('/');
  const taskTab = page.getByTestId(/studio-tab-task:.*ASS-1766/);
  await expect(taskTab).toBeVisible();
  await expect(taskTab.locator('.studio-tab__title')).toHaveText('ASS-1766');
  await expect(taskTab).not.toContainText('C:\\Projects');
  await expect(taskTab).toHaveAttribute('title', 'ASS-1766');
  await expect(taskTab.locator('.studio-tab__title')).toHaveAttribute('title', 'ASS-1766');

  const hubTab = page.getByTestId(/studio-tab-hub:Agent Software Studio/);
  const icon = hubTab.locator('app-studio-icon');
  const hubBox = await hubTab.boundingBox();
  const iconBox = await icon.boundingBox();
  expect(hubBox).not.toBeNull();
  expect(iconBox).not.toBeNull();
  expect(Math.abs((iconBox!.y + iconBox!.height / 2) - (hubBox!.y + hubBox!.height / 2))).toBeLessThan(1);

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    await page.locator('.studio-tabbar').screenshot({
      path: path.join(resultsDir, 'studio-tabs-after--mocked.png'),
    });
  }
});
