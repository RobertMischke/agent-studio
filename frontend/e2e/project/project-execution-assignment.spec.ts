import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = process.env.PROJECT_SHELL_RESULTS_DIR?.trim()
  || path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-execution-assignment');

let projectName = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  projectName = paths.find((item) => /agent.?task|software.?studio/i.test(item.name))?.name ?? paths[0].name;
});

test('assigns a remote host and completes the guided readiness probe', async ({ page }) => {
  await page.route('**/api/projects/settings', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        [projectName]: {
          executionHostId: 'local',
          integrationBranch: 'develop',
          maxParallelism: 1,
        },
      }),
    });
  });
  await page.route('**/api/projects/*/execution-host', async (route) => {
    expect(route.request().method()).toBe('PUT');
    expect(route.request().postDataJSON()).toEqual({ hostId: 'hetzner-agent-runner' });
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ executionHostId: 'hetzner-agent-runner' }),
    });
  });

  await page.goto(`/#/projects/${slugFor(projectName)}/settings`);
  const card = page.getByTestId('project-execution-card');
  await expect(card).toBeVisible({ timeout: 10_000 });

  const hostSelect = card.getByTestId('project-execution-host-select');
  await expect(hostSelect).toHaveValue('local');
  await hostSelect.selectOption('hetzner-agent-runner');
  await expect(hostSelect).toHaveValue('hetzner-agent-runner');
  await expect(card.getByTestId('project-execution-selected-host')).toContainText('agent-runner');

  await card.getByTestId('project-execution-probe').click();
  for (const key of ['code', 'branch', 'toolchain', 'noop']) {
    await expect(card.getByTestId(`project-execution-check-${key}`)).toHaveAttribute('data-state', 'passed');
  }
  await expect(card.getByTestId('project-execution-ready')).toContainText('Ready for project execution');

  await card.screenshot({
    path: path.join(SCREENSHOT_DIR, 'project-execution-probe-passed--mocked.png'),
  });
});
