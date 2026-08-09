import { expect, test } from '../fixtures/dev-backend';
import type { Page, TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const resultRoot = process.env['JOB_RESULTS_DIR']?.trim();
  const directory = resultRoot ? path.resolve(resultRoot) : testInfo.outputDir;
  fs.mkdirSync(directory, { recursive: true });
  return path.join(directory, fileName);
}

async function proxyApi(page: Page, backendBaseUrl: string): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname))
      return json({ models: [], source: 'workbench-live-e2e' });
    if (url.pathname === '/api/cli/quota')
      return json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
    if (url.pathname === '/api/cli/usage')
      return json({ at: new Date().toISOString(), sessions: [] });
    if (url.pathname === '/api/crash-recovery/pending')
      return json({ pending: [] });
    const response = await route.fetch({
      url: `${backendBaseUrl}${url.pathname}${url.search}`,
      timeout: 30_000,
    });
    await route.fulfill({ response });
  });
}

test('project and central overviews receive a newly created item without reloading the Tree', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(150_000);
  const watchPathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(watchPathsResponse.ok).toBe(true);
  const watchPaths = await watchPathsResponse.json() as { name: string; rootPath?: string; repositoryPath?: string }[];
  let projectName: string | null = null;
  for (const candidate of watchPaths) {
    const response = await fetch(
      `${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches?history=true`,
    );
    if (!response.ok) continue;
    const body = await response.json() as { items?: { id: string }[] };
    if (body.items?.some(item => item.id === 'workbench-konzept')) {
      projectName = candidate.name;
      break;
    }
  }
  expect(projectName, 'The dev backend must expose the task checkout Workbenches.').not.toBeNull();

  const id = `live-tree-proof-${Date.now().toString(36)}`;
  const probeDir = path.join(devBackend.workspace, 'docs', 'operations', id);
  try {
    await proxyApi(page, devBackend.baseUrl);
    await page.goto('/');
    await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });

    const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
    await expect(projectRow).toBeVisible();
    if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();

    const sectionRow = page.getByTestId(`studio-explorer-project-workbenches-${projectName}`);
    await expect(sectionRow).toBeVisible();
    await sectionRow.click();
    await expect(page).toHaveURL(/\/workbenches(?:&|$)/);
    await expect(page.getByTestId('workbench-overview-scope')).toContainText(projectName!);

    fs.mkdirSync(probeDir, { recursive: true });
    fs.writeFileSync(path.join(probeDir, 'index.html'), `<!doctype html>
<html>
  <head><style>:root { color-scheme: light dark; } body { color: CanvasText; background: Canvas; }</style></head>
  <body>
    <h1>Live creation proof</h1>
    <section data-decision-id="delivery" data-decision-kind="single">
      <strong>Choose delivery</strong>
      <span data-option-id="direct">Direct</span>
      <span data-option-id="staged">Staged</span>
    </section>
  </body>
</html>`);
    fs.writeFileSync(path.join(probeDir, 'workbench.json'), JSON.stringify({
      schemaVersion: 1,
      id,
      title: 'Live creation proof',
      summary: 'Created while the project overview and Explorer Tree are already open.',
      entrypoint: 'index.html',
      status: 'decision-pending',
      phase: 'decision-ready',
      updatedAt: new Date().toISOString(),
      sourceTaskKeys: [],
      relatedTaskKeys: [],
    }, null, 2));

    const treeItem = page.getByTestId(`studio-explorer-workbench-${projectName}-${id}`);
    await expect(treeItem, 'SignalR created event must add the Tree child without page.reload().')
      .toBeVisible({ timeout: 15_000 });
    await expect(treeItem).toContainText('1 open');
    await expect(page.getByTestId(`workbench-overview-item-${projectName}-${id}`)).toBeVisible();

    const overviewUrl = page.url();
    await page.getByTestId(`workbench-overview-open-${projectName}-${id}`).click();
    await expect(page).toHaveURL(overviewUrl);
    const inlineViewer = page.getByTestId(`workbench-overview-inline-${projectName}-${id}`);
    await expect(inlineViewer).toBeVisible();
    await expect(inlineViewer.frameLocator('[data-testid="workbench-viewer-frame"]')
      .locator('[data-studio-decision-control]')).toHaveCount(2);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-project-overview-${theme}--real.png`),
        fullPage: true,
      });
    }

    await page.getByTestId('studio-ab-workbenches').click();
    await expect(page).toHaveURL(/#\/workbenches(?:&|$)/);
    await expect(page.getByTestId('workbench-overview-scope')).toHaveCount(0);
    await expect(page.getByTestId(`workbench-overview-item-${projectName}-${id}`)).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-central-overview-${theme}--real.png`),
        fullPage: true,
      });
    }
  } finally {
    fs.rmSync(probeDir, { recursive: true, force: true });
  }
});
