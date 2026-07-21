import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};

const HUB_TABS = Array.from({ length: 9 }, (_, index) => ({
  kind: 'hub',
  projectName: `Long project ${index + 1}`,
  section: 'overview',
}));

type StoredTab = Record<string, unknown>;

async function bootWithTabs(page: Page, tabs: StoredTab[], activeKey: string): Promise<void> {
  await page.setViewportSize({ width: 900, height: 700 });
  await page.addInitScript(() => {
    const calls: Array<{ key?: string; options?: ScrollIntoViewOptions }> = [];
    Object.defineProperty(window, '__studioTabScrollCalls', { value: calls });
    const nativeScrollIntoView = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = function (options?: boolean | ScrollIntoViewOptions): void {
      calls.push({
        key: (this as HTMLElement).dataset['tabKey'],
        options: typeof options === 'object' ? options : undefined,
      });
      nativeScrollIntoView.call(this, options);
    };
  });
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (url.includes('/api/tasks/grouped')) return json(EMPTY_GROUPED);
    if (url.includes('/api/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/watch-paths')) {
      return json(HUB_TABS.map((tab, index) => ({
        name: tab.projectName,
        path: `/mock/project-${index + 1}`,
      })));
    }
    if (url.includes('/api/workspaces')) return json([]);
    return route.continue();
  });
  await page.addInitScript(({ storedTabs, storedActiveKey }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: storedTabs,
      activeKey: storedActiveKey,
    }));
  }, { storedTabs: tabs, storedActiveKey: activeKey });

  await page.goto('/');
  await expect(page.getByTestId('studio-tab-list')).toBeVisible({ timeout: 30_000 });
}

async function expectInsideStrip(tab: Locator, strip: Locator): Promise<void> {
  await expect.poll(async () => {
    const [tabBox, stripBox] = await Promise.all([tab.boundingBox(), strip.boundingBox()]);
    if (!tabBox || !stripBox) return false;
    return tabBox.x >= stripBox.x - 1
      && tabBox.x + tabBox.width <= stripBox.x + stripBox.width + 1;
  }).toBe(true);
}

async function expectSmoothNearestScroll(page: Page, key: string): Promise<void> {
  await expect.poll(() => page.evaluate(expectedKey => {
    const calls = (window as typeof window & {
      __studioTabScrollCalls?: Array<{ key?: string; options?: ScrollIntoViewOptions }>;
    }).__studioTabScrollCalls ?? [];
    return calls.some(call => call.key === expectedKey
      && call.options?.behavior === 'smooth'
      && call.options.block === 'nearest'
      && call.options.inline === 'nearest');
  }, key)).toBe(true);
}

async function captureStrip(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const fileName = `${name}--mocked.png`;
  const path = process.env.JOB_RESULTS_DIR
    ? `${process.env.JOB_RESULTS_DIR}/${fileName}`
    : testInfo.outputPath(fileName);
  await page.getByTestId('studio-tabbar').screenshot({ path });
  await testInfo.attach(name, { path, contentType: 'image/png' });
}

test.describe('studio-shell · active tab remains visible in the scrolling strip', () => {
  test('new tab appended at the end scrolls into view', async ({ page }, testInfo) => {
    await bootWithTabs(page, HUB_TABS, 'hub:Long project 1');

    await page.getByTestId('studio-ab-settings').click();

    const active = page.getByTestId('studio-tab-workspace-settings');
    await expect(active).toHaveAttribute('aria-selected', 'true');
    await expectSmoothNearestScroll(page, 'workspace-settings');
    await expectInsideStrip(active, page.getByTestId('studio-tab-list'));
    await captureStrip(page, testInfo, 'active-tab-new-at-end-visible');
  });

  test('navigation to an existing off-screen tab scrolls it into view', async ({ page }, testInfo) => {
    const settings = { kind: 'workspace-settings' };
    const tabs = [settings, ...HUB_TABS];
    await bootWithTabs(page, tabs, 'hub:Long project 9');

    await page.getByTestId('studio-ab-settings').click();

    const active = page.getByTestId('studio-tab-workspace-settings');
    await expect(active).toHaveAttribute('aria-selected', 'true');
    await expectSmoothNearestScroll(page, 'workspace-settings');
    await expectInsideStrip(active, page.getByTestId('studio-tab-list'));
    await captureStrip(page, testInfo, 'active-tab-existing-visible');
  });

  test('closing the active tab keeps its selected neighbour visible', async ({ page }, testInfo) => {
    const tabs = [...HUB_TABS, { kind: 'workspace-settings' }];
    await bootWithTabs(page, tabs, 'workspace-settings');
    const closing = page.getByTestId('studio-tab-workspace-settings');
    await expectInsideStrip(closing, page.getByTestId('studio-tab-list'));

    await closing.getByRole('button', { name: 'Close tab' }).click();

    const neighbour = page.getByTestId('studio-tab-hub:Long project 9');
    await expect(neighbour).toHaveAttribute('aria-selected', 'true');
    await expectSmoothNearestScroll(page, 'hub:Long project 9');
    await expectInsideStrip(neighbour, page.getByTestId('studio-tab-list'));
    await captureStrip(page, testInfo, 'active-tab-close-neighbour-visible');
  });
});
