import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';

interface WatchPath { name: string; path: string; rootPath: string; }

const resultsDir = path.resolve(process.env['JOB_RESULTS_DIR'] ?? 'test-results');

async function setTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate(selectedTheme => {
    localStorage.setItem('atp.studio.theme', selectedTheme);
    document.documentElement.setAttribute('data-studio-theme', selectedTheme);
  }, theme);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
}

test('active board filter stays visible and clears by X, Escape, and an unfiltered route', async ({ page, devBackend }, testInfo) => {
  const watchPathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(watchPathsResponse.ok).toBe(true);
  const watchPaths = await watchPathsResponse.json() as WatchPath[];
  test.skip(watchPaths.length === 0, 'No project is available for the project-filter route assertion.');

  await page.route('**/update/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ phase: 'idle', isRunning: false, behindBy: 0 }),
  }));
  await page.route('**/api/crash-recovery/pending**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  // Keep the integration-filter result deterministic. This evidence is named
  // --mocked because the alert snapshot, and therefore its zero matches, is mocked.
  await page.route('**/api/pipeline/accepted-integration-alert', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      active: false,
      stalledTaskCount: 0,
      thresholdMinutes: 30,
      oldestAcceptedAt: null,
      observedAt: '2026-08-10T12:00:00Z',
      items: [],
    }),
  }));
  await page.addInitScript(() => {
    localStorage.setItem('activeProjects', '[]');
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });

  await page.goto('/#/board&filters=integration%3Astalled');
  const filters = page.getByTestId('board-active-filters');
  const chip = filters.getByTestId('board-active-filter-chip');
  await expect(chip).toContainText('integration:stalled');
  await expect(page.getByTestId('board-filter-empty-hint'))
    .toContainText('0 tasks for filter integration:stalled');

  mkdirSync(resultsDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const screenshotPath = path.join(resultsDir, `board-filter-zero-state--${theme}--mocked.png`);
    await page.screenshot({ path: screenshotPath, fullPage: false });
    await testInfo.attach(`board-filter-zero-state--${theme}--mocked.png`, {
      path: screenshotPath,
      contentType: 'image/png',
    });
  }

  await page.getByRole('button', { name: 'Remove filter integration:stalled' }).click();
  await expect(filters).toHaveCount(0);
  await expect.poll(() => decodeURIComponent(new URL(page.url()).hash)).toBe('#/board');

  await page.evaluate(() => { window.location.hash = '#/board&filters=integration%3Astalled'; });
  await expect(chip).toContainText('integration:stalled');
  await page.keyboard.press('Escape');
  await expect(filters).toHaveCount(0);
  await expect.poll(() => decodeURIComponent(new URL(page.url()).hash)).toBe('#/board');

  const project = watchPaths[0].name;
  await page.evaluate(projectName => {
    window.location.hash = `#/board&filters=${encodeURIComponent(`projects:${projectName}`)}`;
  }, project);
  await expect(filters.getByTestId('board-active-filter-chip')).toContainText(`projects:${project}`);

  await page.evaluate(() => { window.location.hash = '#/board'; });
  await expect(filters).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => localStorage.getItem('activeProjects'))).toBe('[]');
});
