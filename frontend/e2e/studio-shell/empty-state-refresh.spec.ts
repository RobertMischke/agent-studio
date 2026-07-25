import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

const resultsDir = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'empty-state')
  : path.join('test-results', 'empty-state-refresh');

async function openEmptyState(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (url.includes('/api/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/tasks/grouped')) return json(EMPTY_GROUPED);
    if (url.includes('/api/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/crash-recovery/pending')) return json({ pending: [] });
    if (url.includes('/api/watch-paths')) {
      return json([{
        name: 'Agent Software Studio',
        path: '/workspace/agent-software-studio',
        rootPath: '/workspace/agent-software-studio',
        repositoryPath: '/workspace/agent-software-studio',
      }]);
    }
    return route.continue();
  });

  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
  let tabCount = await page.locator('.studio-tab__close').count();
  while (tabCount > 0) {
    await page.locator('.studio-tab__close').first().click();
    await expect(page.locator('.studio-tab__close')).toHaveCount(--tabCount);
  }
  await expect(page.getByTestId('studio-welcome')).toBeVisible();
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate(selectedTheme => {
    document.documentElement.setAttribute('data-studio-theme', selectedTheme);
  }, theme);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
}

test.describe('studio-shell · refreshed empty state', () => {
  test.setTimeout(45_000);

  test('makes chat primary and captures both themes plus the animation cycle', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openEmptyState(page);
    fs.mkdirSync(resultsDir, { recursive: true });

    const welcome = page.getByTestId('studio-welcome');
    const automata = page.getByTestId('studio-empty-state');
    const capture = process.env.EMPTY_STATE_CAPTURE ?? 'after';

    if (capture !== 'before') {
      await expect(page.getByTestId('studio-empty-subtitle'))
        .toHaveText('404 tabs found. Have some cellular automata instead.');
      await expect(page.getByTestId('studio-welcome-chat-hint'))
        .toContainText('Describe your first task in the project chat.');
      await expect(page.getByTestId('studio-welcome-open-chat')).toBeVisible();
      await expect(page.getByTestId('studio-welcome-add-task')).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'New task', exact: true })).toHaveCount(0);

      const canvasBox = await page.getByTestId('studio-empty-canvas').boundingBox();
      expect(canvasBox?.width).toBeGreaterThan(500);

      await setTheme(page, 'dark');
      const frames = [
        { phase: 'chaos', minimumProgress: 0 },
        { phase: 'forming', minimumProgress: 0.55 },
        { phase: 'smiley', minimumProgress: 0.9 },
        { phase: 'decay', minimumProgress: 0.45 },
      ];
      for (const [index, { phase, minimumProgress }] of frames.entries()) {
        await expect(automata).toHaveAttribute('data-phase', phase, { timeout: 15_000 });
        if (minimumProgress > 0) {
          await expect.poll(async () => Number(await automata.getAttribute('data-progress')))
            .toBeGreaterThan(minimumProgress);
        }
        await automata.screenshot({
          path: path.join(resultsDir, `cycle-${index + 1}-${phase}.png`),
        });
      }
    }

    if (capture !== 'before') {
      await expect(automata).toHaveAttribute('data-phase', 'smiley', { timeout: 15_000 });
    }
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await welcome.screenshot({
        path: path.join(resultsDir, `${capture}-${theme}.png`),
      });
    }
  });
});
