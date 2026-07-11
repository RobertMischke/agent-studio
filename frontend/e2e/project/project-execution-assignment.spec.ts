import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'fs';
import * as path from 'path';

const SCREENSHOT_DIR = process.env.PROJECT_SHELL_RESULTS_DIR?.trim()
  || path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-execution-assignment');

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test('assigns a remote host and completes the guided readiness probe', async ({ page, devBackend }) => {
  expect(devBackend.workspace).toBeTruthy();
  const projectName = 'Agent Studio';
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: projectName, path: devBackend.workspace, rootPath: devBackend.workspace }]),
    });
  });
  await page.route('**/api/projects/settings', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        [projectName]: {
          executionRunner: null,
          remoteExecutionEnabled: true,
          integrationBranch: 'develop',
          maxParallelism: 1,
        },
      }),
    });
  });
  await page.route('**/api/projects/*/execution-runner', async (route) => {
    expect(route.request().method()).toBe('PUT');
    expect(route.request().postDataJSON()).toEqual({
      executionRunner: 'agent-runner-01',
      remoteExecutionEnabled: true,
    });
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ executionRunner: 'agent-runner-01', remoteExecutionEnabled: true }),
    });
  });

  await page.goto(`/#/projects/${slugFor(projectName)}/settings`);
  const card = page.getByTestId('project-execution-card');
  await expect(card).toBeVisible({ timeout: 10_000 });

  const hostSelect = card.getByTestId('project-execution-host-select');
  await expect(hostSelect).toHaveValue('local');
  await hostSelect.selectOption('agent-runner-01');
  await expect(hostSelect).toHaveValue('agent-runner-01');
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
