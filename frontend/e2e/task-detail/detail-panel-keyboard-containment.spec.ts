import { expect, test, type Locator, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, moveJob, waitForJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

interface WatchPath { path: string }

const REVIEW_LANE = '5-human-review';

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function removeTask(id: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => undefined);
}

async function makeScrollable(surface: Locator): Promise<void> {
  await surface.evaluate((element) => {
    element.querySelector('[data-testid="keyboard-scroll-fixture"]')?.remove();
    const filler = document.createElement('div');
    filler.dataset['testid'] = 'keyboard-scroll-fixture';
    const fillerHeight = Math.max(element.clientHeight * 4, 2400);
    filler.style.flex = `0 0 ${fillerHeight}px`;
    filler.style.minHeight = `${fillerHeight}px`;
    filler.setAttribute('aria-hidden', 'true');
    element.append(filler);
    element.scrollTop = 0;
  });
  await expect.poll(() => surface.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(true);
}

async function currentTaskKey(page: Page): Promise<string | null> {
  const key = page.getByTestId('detail-key-chip');
  if (!await key.count()) return null;
  return (await key.textContent())?.trim() ?? null;
}

async function expectArrowScrollWithoutTaskChange(
  page: Page,
  tabTestId: string,
  surfaceTestId: string,
  taskKey: string,
): Promise<void> {
  const tab = page.getByTestId(tabTestId);
  await expect(tab).toBeEnabled();
  await tab.click();

  const surface = page.getByTestId(surfaceTestId);
  await makeScrollable(surface);
  await surface.focus();
  await expect(surface).toBeFocused();

  await page.keyboard.press('ArrowDown');

  await expect.poll(() => surface.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
  expect(await currentTaskKey(page)).toBe(taskKey);
}

test.describe('Task detail panel keyboard containment', () => {
  test('detail tabs own vertical scrolling while board focus keeps task paging', async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const prefix = `e2e-keyboard-containment-${Date.now()}`;
    const taskIds = [`${prefix}-a`, `${prefix}-b`, `${prefix}-c`];

    try {
      for (const id of taskIds) {
        await createJob({
          id,
          title: id,
          watchPath,
          promptMarkdown: `# ${id}\n\nKeyboard containment fixture.`,
          targetState: '0-backlog',
          fixture: false,
        });
        await moveJob(id, watchPath, REVIEW_LANE);
        await waitForJob(id, watchPath, (task) => task.state === REVIEW_LANE, { timeoutMs: 20_000 });
      }

      await page.addInitScript(() => {
        localStorage.setItem('atp.flag.vsCodeLayout', '0');
        localStorage.removeItem('taskboard.panesVisible');
      });
      await page.route('**/api/crash-recovery/pending', async (route) => {
        if (route.request().method() === 'GET') await route.fulfill({ json: { pending: [] } });
        else await route.continue();
      });
      await page.setViewportSize({ width: 1600, height: 960 });
      await page.goto(`/?job=${encodeURIComponent(taskIds[1])}&watchPath=${encodeURIComponent(watchPath)}`);

      const promptSurface = page.getByTestId('prompt-tab-surface');
      const inspectorSurface = page.getByTestId('inspector-tab-surface');
      await expect(inspectorSurface).toBeVisible({ timeout: 15_000 });
      await expect(inspectorSurface).toBeFocused();
      const initialTaskKey = await currentTaskKey(page);
      expect(initialTaskKey).not.toBeNull();

      for (const [tab, surface] of [
        ['inspector-tab-task', 'inspector-tab-surface'],
        ['inspector-tab-activity', 'inspector-tab-surface'],
        ['inspector-tab-protocol', 'inspector-tab-surface'],
        ['prompt-tab-evidence', 'prompt-tab-surface'],
        ['prompt-tab-timeline', 'prompt-tab-surface'],
      ] as const) {
        await expectArrowScrollWithoutTaskChange(page, tab, surface, initialTaskKey!);
      }

      await page.getByTestId('inspector-tab-activity').click();
      await makeScrollable(inspectorSurface);
      await inspectorSurface.focus();
      const maxScroll = await inspectorSurface.evaluate(
        (element) => element.scrollHeight - element.clientHeight,
      );

      await page.keyboard.press('End');
      await expect.poll(() => inspectorSurface.evaluate((element) => element.scrollTop))
        .toBeGreaterThan(maxScroll * 0.8);
      await page.keyboard.press('Home');
      await expect.poll(() => inspectorSurface.evaluate((element) => element.scrollTop)).toBe(0);
      await page.keyboard.press('PageDown');
      await expect.poll(() => inspectorSurface.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
      const afterPageDown = await inspectorSurface.evaluate((element) => element.scrollTop);
      await page.keyboard.press('PageUp');
      await expect.poll(() => inspectorSurface.evaluate((element) => element.scrollTop))
        .toBeLessThan(afterPageDown);
      await inspectorSurface.evaluate((element) => { element.scrollTop = 300; });
      await page.keyboard.press('ArrowUp');
      await expect.poll(() => inspectorSurface.evaluate((element) => element.scrollTop)).toBeLessThan(300);
      expect(await currentTaskKey(page)).toBe(initialTaskKey);

      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        const screenshotName = `detail-panel-keyboard-containment-${theme}--mocked.png`;
        await testInfo.attach(screenshotName, {
          body: await page.screenshot({ fullPage: false }),
          contentType: 'image/png',
        });
        if (process.env.JOB_RESULTS_DIR) {
          await page.screenshot({
            path: `${process.env.JOB_RESULTS_DIR}/${screenshotName}`,
            fullPage: false,
          });
        }
      }

      const currentTaskButton = page.getByRole('button').filter({ hasText: taskIds[1] });
      await expect(currentTaskButton).toBeVisible();
      await currentTaskButton.focus();
      await page.keyboard.press('ArrowDown');
      await expect.poll(() => currentTaskKey(page)).not.toBe(initialTaskKey);

      const pagedTaskKey = await currentTaskKey(page);
      await promptSurface.focus();
      await page.keyboard.press('Tab');
      await expect(promptSurface).not.toBeFocused();
      expect(await currentTaskKey(page)).toBe(pagedTaskKey);

      await page.keyboard.press('Escape');
      await expect(page.getByTestId('kanban-dashboard')).toBeVisible();
      expect(await currentTaskKey(page)).toBeNull();
    } finally {
      for (const id of taskIds) await removeTask(id, watchPath);
    }
  });
});
