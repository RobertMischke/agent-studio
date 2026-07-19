import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath?: string }

const SCREENSHOTS = path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-steering-docs');
fs.mkdirSync(SCREENSHOTS, { recursive: true });

async function openSteeringRail(page: Page, projectName: string): Promise<void> {
  await page.goto('/');
  const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(projectRow).toBeVisible({ timeout: 10_000 });

  const hubRow = page.getByTestId(`studio-explorer-project-hub-${projectName}`);
  if (!(await hubRow.isVisible().catch(() => false))) {
    await projectRow.click();
  }
  await expect(hubRow).toBeVisible({ timeout: 10_000 });
  await hubRow.click();

  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  const rail = page.getByTestId('project-shell-rail-steering');
  if (!(await rail.isVisible().catch(() => false))) {
    await page.getByTestId('project-shell-group-context').click();
  }
  await expect(rail).toBeVisible();
  await rail.click();
  await expect(rail).toHaveAttribute('aria-current', 'page');
}

async function mockSteeringApi(page: Page, projectName: string, watchPath: string): Promise<void> {
  await page.route('**/api/watch-paths', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: projectName, path: watchPath, rootPath: watchPath }]),
  }));

  const overview = {
    projectName,
    baseDir: 'C:/Projects/demo',
    lastUpdated: '2026-06-23T10:00:00Z',
    sources: [
      {
        id: 'agents-md',
        label: 'AGENTS.md',
        relPath: 'AGENTS.md',
        kind: 'agentInstructions',
        why: 'Project-level agent instructions loaded from the repository root.',
        exists: true,
        updatedAt: '2026-06-23T10:00:00Z',
        size: 2400,
        appliesToClis: ['codex', 'claude', 'copilot'],
        children: null,
      },
      {
        id: 'frontend-agents-md',
        label: 'AGENTS.md',
        relPath: 'frontend/AGENTS.md',
        kind: 'agentInstructions',
        why: 'Frontend-scoped agent instructions loaded for work below this folder.',
        exists: true,
        updatedAt: '2026-06-22T10:00:00Z',
        size: 800,
        appliesToClis: ['codex', 'claude', 'copilot'],
        children: null,
      },
      {
        id: 'github-copilot-instructions-md',
        label: 'copilot-instructions.md',
        relPath: '.github/copilot-instructions.md',
        kind: 'agentCliShim',
        why: 'GitHub Copilot coding-agent instruction file.',
        exists: true,
        updatedAt: '2026-06-21T10:00:00Z',
        size: 220,
        appliesToClis: ['copilot'],
        children: null,
      },
    ],
    warnings: [{
      severity: 'warn',
      kind: 'gatewayTooHeavy',
      message: 'AGENTS.md carries 2,400 bytes of local instructions but links to only 0 wiki pages. Agent docs should stay gateway-style and route durable detail into the project wiki.',
      sourceId: 'agents-md',
      evidenceRefs: ['AGENTS.md', 'docs/'],
    }],
  };

  const readAnalytics = {
    projectName,
    baseDir: 'C:/Projects/demo',
    windowDays: 7,
    hasData: true,
    totalReads: 9,
    recentReads: 4,
    taskCount: 3,
    lastReadAt: '2026-06-23T09:00:00Z',
    files: [
      {
        relPath: 'AGENTS.md',
        label: 'AGENTS.md',
        reads: 6,
        recentReads: 3,
        taskCount: 2,
        lastReadAt: '2026-06-23T09:00:00Z',
        byCli: [{ cli: 'claude', reads: 4 }, { cli: 'codex', reads: 2 }],
      },
      {
        relPath: 'frontend/AGENTS.md',
        label: 'AGENTS.md',
        reads: 3,
        recentReads: 1,
        taskCount: 1,
        lastReadAt: '2026-06-22T09:00:00Z',
        byCli: [{ cli: 'codex', reads: 3 }],
      },
      {
        relPath: '.github/copilot-instructions.md',
        label: 'copilot-instructions.md',
        reads: 0,
        recentReads: 0,
        taskCount: 0,
        lastReadAt: null,
        byCli: [],
      },
    ],
    byCli: [{ cli: 'claude', reads: 4 }, { cli: 'codex', reads: 5 }],
    generatedAt: '2026-06-23T10:00:00Z',
  };

  // Real Tool-Use Read Analytics behind the former mockup.
  await page.route('**/api/projects/*/steering/read-analytics**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(readAnalytics),
  }));

  await page.route('**/api/projects/*/steering', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(overview),
  }));

  await page.route('**/api/projects/*/steering/files/**', route => {
    const marker = '/steering/files/';
    const url = new URL(route.request().url());
    const relPath = decodeURIComponent(url.pathname.slice(url.pathname.indexOf(marker) + marker.length));
    const content = relPath === 'frontend/AGENTS.md'
      ? '# Frontend agent rules\n\nFrontend rules route durable details into docs/.'
      : '# Root agent rules\n\nUse AGENTS.md as a gateway to docs/ pages.';
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ relPath, content }),
    });
  });
}

test.describe('Project detail - Agent Docs section', () => {
  let projectName = '';
  let watchPath = '';

  test.beforeAll(async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);
    const preferred = paths.find(p => /agent.?software|agent.?task|runbook/i.test(p.name)) ?? paths[0];
    projectName = preferred.name;
    watchPath = preferred.path;
  });

  test.beforeEach(async ({ page }) => {
    await mockSteeringApi(page, projectName, watchPath);
  });

  test('tree shows existing agent docs, CLI scope, gateway warning, and real tool-use read analytics', async ({ page }) => {
    await openSteeringRail(page, projectName);
    const panel = page.getByTestId('project-shell-panel-steering');
    await expect(panel).toBeVisible();

    const section = panel.getByTestId('project-steering-docs-section');
    await expect(section).toBeVisible({ timeout: 10_000 });
    await expect(panel.getByTestId('project-steering-docs-tree')).toBeVisible();
    await expect(panel.getByTestId('project-steering-docs-tree')).toContainText('frontend');
    await expect(panel.getByTestId('project-steering-docs-tree')).not.toContainText('README.md');
    await expect(panel.getByTestId('project-skill-readiness-section')).toHaveCount(0);

    await expect(panel.getByTestId('project-steering-docs-viewer-path')).toContainText('AGENTS.md');
    await expect(panel.getByTestId('project-steering-docs-viewer-clis')).toContainText('Codex');
    await expect(panel.getByTestId('project-steering-docs-viewer-clis')).toContainText('Claude Code');
    await expect(panel.getByTestId('project-steering-docs-selected-warnings')).toContainText('gateway-style');
    await expect(panel.getByTestId('project-steering-docs-content')).toContainText('Root agent rules');

    await panel.getByTestId('project-steering-docs-file-frontend/AGENTS.md').click();
    await expect(panel.getByTestId('project-steering-docs-viewer-path')).toContainText('frontend/AGENTS.md');
    await expect(panel.getByTestId('project-steering-docs-content')).toContainText('Frontend agent rules');

    // Real Tool-Use Read Analytics replaced the mockup: live totals, per-file
    // rows, and per-CLI counts. No "Mockup" pill, no fabricated numbers.
    const usage = panel.getByTestId('project-steering-docs-tool-use');
    await expect(usage).toBeVisible();
    await expect(usage).not.toContainText('Mockup');
    await expect(panel.getByTestId('project-steering-docs-tool-use-live')).toContainText('9 reads');
    const rootRow = panel.getByTestId('project-steering-docs-tool-use-row-AGENTS.md');
    await expect(rootRow).toContainText('Claude Code 4');
    await expect(rootRow).toContainText('Codex 2');
    // A zero-read inventory file is not rendered as a usage row.
    await expect(panel.getByTestId('project-steering-docs-tool-use-row-.github/copilot-instructions.md')).toHaveCount(0);

    await page.screenshot({
      path: path.join(SCREENSHOTS, '01-inventory-and-summary.png'),
      fullPage: true,
    });
  });

  test('renders an honest empty state when no reads are indexed yet', async ({ page }) => {
    await page.unroute('**/api/projects/*/steering/read-analytics**');
    await page.route('**/api/projects/*/steering/read-analytics**', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName, baseDir: 'C:/Projects/demo', windowDays: 7, hasData: false,
        totalReads: 0, recentReads: 0, taskCount: 0, lastReadAt: null,
        files: [], byCli: [], generatedAt: '2026-06-23T10:00:00Z',
      }),
    }));

    await openSteeringRail(page, projectName);
    const panel = page.getByTestId('project-shell-panel-steering');
    await expect(panel.getByTestId('project-steering-docs-section')).toBeVisible({ timeout: 10_000 });

    await expect(panel.getByTestId('project-steering-docs-tool-use-nodata')).toContainText('No data yet');
    await expect(panel.getByTestId('project-steering-docs-tool-use-empty')).toContainText('No indexed tool-use reads');
    await expect(panel.getByTestId('project-steering-docs-tool-use-live')).toHaveCount(0);

    await page.screenshot({
      path: path.join(SCREENSHOTS, '04-tool-use-empty-state.png'),
      fullPage: true,
    });
  });

  test('unknown project API still returns a helpful 404', async ({ page }) => {
    await page.unroute('**/api/projects/*/steering');
    const res = await page.request.get('/api/projects/__steering-docs-no-such-project__/steering');
    expect(res.status()).toBe(404);
  });
});
