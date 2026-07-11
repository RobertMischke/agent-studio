import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const results = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.join(__dirname, '..', '..', 'test-results', 'project-source-screenshots');

const sources = [
  { id: 'local-folder', label: 'Local folder', available: true, description: 'A checkout available on this Agent Studio host.' },
  { id: 'remote-git', label: 'Remote Git', available: false, description: 'Prepared for a future managed remote checkout.' },
  { id: 'cloud', label: 'Cloud workspace', available: false, description: 'Prepared for a future cloud provider integration.' },
];

test.describe('project source onboarding and administration', () => {
  test.beforeEach(async ({ page }) => {
    fs.mkdirSync(results, { recursive: true });
    await page.route('**/api/project-sources', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(sources),
    }));
    await page.route('**/api/workspaces**', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'ws-default', displayName: 'Default', sortOrder: 0, archived: false,
        createdAt: '2026-01-01T00:00:00Z', projects: [],
      }]),
    }));
    await page.route('**/api/clients', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  test('admin lists available and prepared project sources', async ({ page }) => {
    await page.goto('/#/workspace/settings/project-sources');
    await expect(page.getByTestId('workspace-project-sources')).toBeVisible();
    await expect(page.getByTestId('project-source-local-folder')).toContainText('Available');
    await expect(page.getByTestId('project-source-remote-git')).toContainText('Coming soon');
    await expect(page.getByTestId('project-source-cloud')).toContainText('Coming soon');
    await page.screenshot({ path: path.join(results, 'project-sources-admin.png'), fullPage: true });
  });

  test('onboarding controls remain inside the dialog and expose source choices', async ({ page }) => {
    await page.goto('/');
    const add = page.locator('[data-testid^="studio-workspace-"][data-testid$="-add-project"]').first();
    await expect(add).toBeVisible();
    if (await page.getByTestId('error-dialog-overlay').isVisible().catch(() => false)) {
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('error-dialog-overlay')).not.toBeVisible();
    }
    await add.click();

    const dialog = page.getByTestId('onboard-project-dialog');
    await expect(dialog).toBeVisible();
    await expect(page.getByTestId('onboard-project-source')).toHaveValue('local-folder');
    const contained = await dialog.evaluate(element => {
      const dialogBox = element.getBoundingClientRect();
      return [...element.querySelectorAll('input, select')].every(control => {
        const box = control.getBoundingClientRect();
        return box.left >= dialogBox.left && box.right <= dialogBox.right;
      });
    });
    expect(contained).toBe(true);
    await dialog.screenshot({ path: path.join(results, 'onboarding-dialog-contained-fields--mocked.png') });
  });

  test('submits repository and runner fields through the project API', async ({ page }) => {
    let submitted: Record<string, unknown> | null = null;
    await page.route('**/api/projects', async route => {
      if (route.request().method() !== 'POST') return route.continue();
      submitted = route.request().postDataJSON();
      await route.fulfill({
        status: 201,
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'PROJ-023', displayName: 'Quality Studio', shortCode: 'QS', workspaceId: 'ws-default',
          sourceType: 'local-folder', storageLocation: 'C:/workspace/projects/PROJ-023/tasks',
          repositoryPath: 'C:/Projects/quality-studio', rootPath: 'C:/Projects/quality-studio',
          urls: [], archived: false, createdAt: '2026-07-12T00:00:00Z', sortOrder: 0,
        }),
      });
    });
    await page.goto('/');
    await page.locator('[data-testid^="studio-workspace-"][data-testid$="-add-project"]').first().click();
    await page.getByTestId('onboard-project-display-name').fill('Quality Studio');
    await page.getByTestId('onboard-project-short-code').fill('QS');
    await page.getByTestId('onboard-project-root-path').fill('C:/Projects/quality-studio');
    await page.getByTestId('onboard-project-repository-url').fill('https://github.com/example/quality-studio');
    const runner = page.getByTestId('onboard-project-runner');
    const remoteValue = 'agent-runner-01';
    await expect(runner.locator(`option[value="${remoteValue}"]`)).toHaveCount(1);
    await runner.selectOption(remoteValue);
    await page.getByTestId('onboard-project-dialog').screenshot({
      path: path.join(results, 'onboarding-project-api-payload--mocked.png'),
    });
    await page.getByTestId('onboard-project-submit').click();

    await expect(page.getByTestId('onboard-project-dialog')).not.toBeVisible();
    expect(submitted).toMatchObject({
      displayName: 'Quality Studio', shortCode: 'QS', workspaceId: 'ws-default',
      repositoryPath: 'C:/Projects/quality-studio', rootPath: 'C:/Projects/quality-studio',
      repositoryUrl: 'https://github.com/example/quality-studio', executionRunner: remoteValue,
    });
  });
});
