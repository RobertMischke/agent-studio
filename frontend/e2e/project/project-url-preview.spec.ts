import { expect, test, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT_ID = 'PROJ-URL-PREVIEW';
const PROJECT_NAME = 'Coding Agent Chat';
const URL_ID = 'library-website';
const RESULTS_DIR = path.resolve(process.cwd(), '..', 'results');

function workspacePayload(url: string) {
  return [{
    id: 'ws-preview', displayName: 'Preview fixtures', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-07-12T00:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: PROJECT_ID, displayName: PROJECT_NAME,
      shortCode: 'CAC', workspaceId: 'ws-preview', color: null, cliDefault: null,
      modelDefault: null, sortOrder: 0, storageLocation: '/tmp/coding-agent-chat/tasks',
      repositoryPath: '/tmp/coding-agent-chat', rootPath: '/tmp/coding-agent-chat',
      repositoryUrl: null, archived: false, createdAt: '2026-07-12T00:00:00Z',
      urls: [{ id: URL_ID, label: 'Library Website', url, sortOrder: 0, startRule: null }],
    }],
  }];
}

/** Backend readiness verdict served to the tab; the backend probe cannot see
 *  browser-side `page.route` fixtures, so each scenario states its own. */
const HEALTHY_READINESS = {
  kind: 'healthy', statusCode: 200, framePolicy: 'allowed', detail: null, durationMs: 3,
};
const OFFLINE_READINESS = {
  kind: 'offline', statusCode: null, framePolicy: 'unknown', detail: 'Connection refused.', durationMs: 3,
};

async function openPreview(
  page: Page,
  url: string,
  readiness: Record<string, unknown> = HEALTHY_READINESS,
): Promise<void> {
  await page.addInitScript(({ projectName, urlId }) => {
    const activeKey = `url-preview:${projectName}:${urlId}`;
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'url-preview', projectName, urlId }], activeKey,
    }));
  }, { projectName: PROJECT_NAME, urlId: URL_ID });
  await page.route('**/api/watch-paths**', route => route.fulfill({ json: [{
    name: PROJECT_NAME, path: '/tmp/coding-agent-chat/tasks',
    rootPath: '/tmp/coding-agent-chat', repositoryPath: '/tmp/coding-agent-chat',
  }] }));
  await page.route('**/api/workspaces**', route => route.fulfill({ json: workspacePayload(url) }));
  await page.route(`**/api/projects/${PROJECT_ID}/urls/${URL_ID}/readiness**`,
    route => route.fulfill({ json: readiness }));
  await page.route(`**/api/projects/${PROJECT_ID}/urls/${URL_ID}/process`,
    route => route.request().method() === 'GET'
      ? route.fulfill({ status: 204, body: '' })
      : route.continue());
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await expect(page.getByTestId('url-preview-tab')).toBeVisible();
}

async function screenshotThemes(page: Page, stem: string): Promise<void> {
  await mkdir(RESULTS_DIR, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    await page.screenshot({ path: path.join(RESULTS_DIR, `${stem}--${theme}.png`), fullPage: true });
  }
}

test.describe('Project URL preview readiness', () => {
  test('healthy same-origin content is confirmed and Reload performs another navigation', async ({ page }) => {
    const baseUrl = process.env.PW_BASE_URL ?? 'http://localhost:4010';
    const fixtureUrl = new URL('/__url-preview/healthy', baseUrl).href;
    let documentNavigations = 0;
    await page.route(fixtureUrl, route => {
      if (route.request().resourceType() === 'document') documentNavigations += 1;
      return route.fulfill({
        contentType: 'text/html',
        body: '<!doctype html><html><body style="background:#fff;color:#111"><main><h1>Healthy embedded content</h1><p>Frame navigation succeeded.</p></main></body></html>',
      });
    });

    await openPreview(page, fixtureUrl);
    const frame = page.getByTestId('url-preview-frame');
    await expect(frame).toHaveJSProperty('src', fixtureUrl);
    await expect(page.getByTestId('url-preview-status')).toHaveAttribute('data-status', 'rendered');
    await expect(frame.contentFrame().getByText('Healthy embedded content')).toBeVisible();

    await page.getByTestId('url-preview-reload').click();
    await expect.poll(() => documentNavigations).toBeGreaterThanOrEqual(2);
    await expect(frame.contentFrame().getByText('Frame navigation succeeded.')).toBeVisible();
    await screenshotThemes(page, 'project-url-preview-healthy');
  });

  test('empty loaded body shows the blank fallback instead of a healthy pill', async ({ page }) => {
    const baseUrl = process.env.PW_BASE_URL ?? 'http://localhost:4010';
    const fixtureUrl = new URL('/__url-preview/blank', baseUrl).href;
    await page.route(fixtureUrl, route => route.fulfill({
      contentType: 'text/html', body: '<!doctype html><html><body></body></html>',
    }));

    await openPreview(page, fixtureUrl);
    await expect(page.getByTestId('url-preview-blank')).toContainText('loaded body is empty');
    await expect(page.getByTestId('url-preview-inline-reload')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open externally' })).toBeVisible();
    await screenshotThemes(page, 'project-url-preview-blank-fallback');
  });

  test('unreachable navigation resolves to the server-offline state', async ({ page }) => {
    const fixtureUrl = 'http://127.0.0.1:42992/unreachable';
    await page.route(fixtureUrl, route => route.abort('connectionrefused'));

    await openPreview(page, fixtureUrl, OFFLINE_READINESS);
    await expect(page.getByTestId('url-preview-offline')).toBeVisible();
    await expect(page.getByTestId('url-preview-frame')).toHaveCount(0);
  });

  for (const policy of [
    {
      name: 'CSP frame-ancestor',
      headers: { 'Content-Security-Policy': "frame-ancestors 'none'" },
      errorText: 'frame-ancestors',
    },
    {
      name: 'X-Frame-Options',
      headers: { 'X-Frame-Options': 'DENY' },
      errorText: 'x-frame-options',
    },
  ]) test(`${policy.name} denial is explicit and actionable`, async ({ page }) => {
    const baseUrl = process.env.PW_BASE_URL ?? 'http://localhost:4010';
    const slug = policy.name.toLowerCase().replaceAll(' ', '-');
    const fixtureUrl = new URL(`/__url-preview/${slug}-denied`, baseUrl).href;
    const consoleErrors: string[] = [];
    page.on('console', message => {
      if (message.type() === 'error') consoleErrors.push(message.text());
    });
    await page.route(fixtureUrl, route => route.fulfill({
      contentType: 'text/html',
      headers: policy.headers,
      body: '<!doctype html><html><body><h1>Must not render in a frame</h1></body></html>',
    }));

    await openPreview(page, fixtureUrl);
    await expect(page.getByTestId('url-preview-blocked')).toBeVisible();
    expect(consoleErrors.some(message => message.toLowerCase().includes(policy.errorText))).toBe(true);
  });

  test('real Coding Agent Chat website on 4202 renders visible frame content', async ({ page }) => {
    const url = 'http://localhost:4202/';
    const reachable = await fetch(url).then(response => response.ok).catch(() => false);
    test.skip(!reachable, 'Coding Agent Chat website is not running on port 4202.');

    const consoleErrors: string[] = [];
    const sandboxViolations: string[] = [];
    const failedResources: { url: string; error: string }[] = [];
    page.on('console', message => {
      const value = message.text();
      if (message.type() === 'error') consoleErrors.push(value);
      if (value.toLowerCase().includes('sandbox')) sandboxViolations.push(value);
    });
    page.on('requestfailed', request => failedResources.push({
      url: request.url(), error: request.failure()?.errorText ?? 'unknown',
    }));

    await openPreview(page, url);
    const frameElement = page.getByTestId('url-preview-frame');
    await expect(frameElement).toHaveJSProperty('src', url);
    const child = frameElement.contentFrame();
    await expect(child.locator('body')).not.toBeEmpty();
    const evidence = await child.locator('body').evaluate(body => ({
      url: body.ownerDocument.URL,
      width: body.scrollWidth,
      height: body.scrollHeight,
      text: (body.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 500),
    }));
    expect(evidence.url).toBe(url);
    expect(evidence.width).toBeGreaterThan(0);
    expect(evidence.height).toBeGreaterThan(0);
    expect(evidence.text.length).toBeGreaterThan(20);
    await expect(page.getByTestId('url-preview-unconfirmed')).toContainText('browser origin rules');

    await mkdir(RESULTS_DIR, { recursive: true });
    await writeFile(path.join(RESULTS_DIR, 'project-url-preview-live-diagnostics.json'), JSON.stringify({
      configuredUrl: url,
      effectiveIframeUrl: await frameElement.getAttribute('src'),
      body: evidence,
      consoleErrors,
      sandboxViolations,
      failedResources,
      sandbox: await frameElement.getAttribute('sandbox'),
    }, null, 2));
    await screenshotThemes(page, 'project-url-preview-live-4202');
  });
});
