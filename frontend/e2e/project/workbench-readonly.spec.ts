import { expect, test } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const RESULTS_DIR = path.resolve(__dirname, '..', '..', '..', 'results');

async function proxyBackend(page: Page, baseUrl: string): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${baseUrl}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

test('Workbench Explorer, isolated viewer, and Pulse thinking inbox use real repository artifacts in both themes', async ({ page, devBackend }) => {
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok).toBe(true);
  const paths = await pathsResponse.json() as { name: string; rootPath?: string }[];
  let project: { name: string; rootPath?: string } | undefined;
  let createdProjectId: string | null = null;
  let createdWorkspaceId: string | null = null;
  let clientId: string | null = null;
  for (const candidate of paths) {
    const response = await fetch(`${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches`);
    if (!response.ok) continue;
    const catalogue = await response.json() as { items?: { id: string }[] };
    if (catalogue.items?.some(item => item.id === 'app-survey')) { project = candidate; break; }
  }
  if (!project) {
    const clientName = `workbench-evidence-${Date.now().toString(36)}`;
    const register = await fetch(`${devBackend.baseUrl}/api/clients/register`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ displayName: clientName }),
    });
    const client = await register.json() as { id: string };
    clientId = client.id;
    const workspaceResponse = await fetch(`${devBackend.baseUrl}/api/workspaces`);
    const workspaces = await workspaceResponse.json() as { id: string }[];
    if (workspaces.length === 0) {
      const workspaceCreate = await fetch(`${devBackend.baseUrl}/api/workspaces`, {
        method: 'POST', headers: { 'content-type': 'application/json', 'X-Client-Id': client.id },
        body: JSON.stringify({ displayName: 'Workbench Evidence' }),
      });
      const workspace = await workspaceCreate.json() as { id: string };
      createdWorkspaceId = workspace.id;
      workspaces.push(workspace);
    }
    const displayName = `Workbench Evidence ${Date.now().toString(36)}`;
    const response = await fetch(`${devBackend.baseUrl}/api/projects`, {
      method: 'POST', headers: { 'content-type': 'application/json', 'X-Client-Id': client.id },
      body: JSON.stringify({ sourceType: 'local-folder', workspaceId: workspaces[0].id,
        displayName, shortCode: `W${Date.now().toString(36).slice(-5)}`.toUpperCase(), rootPath: devBackend.workspace }),
    });
    const responseBody = await response.text();
    expect(response.ok, responseBody).toBe(true);
    const created = JSON.parse(responseBody) as { id: string; displayName: string };
    createdProjectId = created.id;
    const associate = await fetch(`${devBackend.baseUrl}/api/projects/${created.id}`, {
      method: 'PUT', headers: { 'content-type': 'application/json', 'X-Client-Id': client.id },
      body: JSON.stringify({ repositoryPath: devBackend.workspace }),
    });
    expect(associate.ok, await associate.text()).toBe(true);
    project = { name: created.displayName, rootPath: devBackend.workspace };
  }
  expect(project, 'The real backend must expose this repository as a project.').toBeTruthy();
  try {
  const realCatalogueResponse = await fetch(`${devBackend.baseUrl}/api/projects/${encodeURIComponent(project!.name)}/workbenches`);
  const realCatalogueText = await realCatalogueResponse.text();
  expect(realCatalogueResponse.ok, realCatalogueText).toBe(true);
  expect(realCatalogueText).toContain('app-survey');

  await proxyBackend(page, devBackend.baseUrl);
  await page.goto('/');
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const projectRow = page.getByTestId(`studio-explorer-project-${project.name}`);
  await expect(projectRow).toBeVisible();
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();

  const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${project.name}`);
  await expect(workbenchesRow).toBeVisible();
  await workbenchesRow.click();
  await expect(page.getByTestId(`studio-explorer-workbench-${project.name}-pipeline-workbench`)).toBeVisible();
  await expect(page.getByTestId(`studio-explorer-workbench-${project.name}-workbench-mockup-family`)).toBeVisible();
  await expect(page.getByTestId(`studio-explorer-workbench-${project.name}-app-survey`)).toBeVisible();
  await expect(page.getByTestId(`studio-explorer-workbench-history-${project.name}`)).toBeVisible();

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: path.join(RESULTS_DIR, `workbench-explorer-${theme}.png`), fullPage: true });
  }

  await page.getByTestId(`studio-explorer-workbench-${project.name}-app-survey`).click();
  const frame = page.getByTestId('workbench-viewer-frame');
  await expect(frame).toBeVisible();
  await expect(frame).toHaveAttribute('sandbox', 'allow-scripts');
  const srcdoc = await frame.getAttribute('srcdoc');
  expect(srcdoc).toContain("default-src 'none'");
  expect(srcdoc).toContain("connect-src 'none'");
  expect(srcdoc).not.toContain('allow-same-origin');
  await expect(page.getByTestId('workbench-viewer-provenance')).toContainText('docs/design/app-survey-2026-07-11.html');

  await page.getByTestId(`studio-explorer-project-wiki-${project.name}`).click();
  await expect(page.getByTestId('project-wiki-pulse-workbenches')).toBeVisible();
  await expect(page.getByTestId('project-wiki-pulse-workbench-pipeline-workbench')).toBeVisible();
  await expect(page.getByTestId('project-wiki-pulse-workbench-app-survey')).toBeVisible();
  await page.getByTestId('project-wiki-toggle-nav').click();
  await page.getByTestId('project-wiki-meta-toggle').click();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: path.join(RESULTS_DIR, `workbench-pulse-${theme}.png`), fullPage: true });
  }

  await page.getByTestId('project-wiki-pulse-workbench-pipeline-workbench').click();
  await expect(page.getByTestId('workbench-viewer-provenance')).toContainText('docs/domains/pipeline.md.report.html');
  } finally {
    if (createdProjectId) await fetch(`${devBackend.baseUrl}/api/projects/${createdProjectId}`, {
      method: 'DELETE', headers: { 'X-Client-Id': clientId ?? '' },
    });
    if (createdWorkspaceId) await fetch(`${devBackend.baseUrl}/api/workspaces/${createdWorkspaceId}`, {
      method: 'DELETE', headers: { 'X-Client-Id': clientId ?? '' },
    });
  }
});
