import { expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { test } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const shots = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'url-preview')
  : resolve('test-results/url-preview');

interface Project { id: string; displayName: string; archived: boolean; }
interface UpdatedProject { urls: { id: string; label: string }[]; }

test('offline 4184 is actionable, responsive, and links to quick setup', async ({ page, devBackend }) => {
  await page.route('**/api/**', async route => {
    const requestUrl = new URL(route.request().url());
    try {
      const response = await route.fetch({
        url: `${devBackend.baseUrl}${requestUrl.pathname}${requestUrl.search}`,
      });
      await route.fulfill({ response });
    } catch {
      await route.abort('failed').catch(() => undefined);
    }
  });
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  mkdirSync(shots, { recursive: true });
  const projects = await api<Project[]>('/api/projects');
  const project = projects.find(p => !p.archived);
  test.skip(!project, 'No active registry project is available.');

  const label = `Offline 4184 ${Date.now()}`;
  let urlId = '';
  try {
    const updated = await api<UpdatedProject>(`/api/projects/${project!.id}/urls`, {
      method: 'POST',
      body: JSON.stringify({
        label,
        url: 'http://127.0.0.1:4184',
        startRule: {
          // No cwd: the registry only accepts absolute existing paths, and the
          // scenario needs a startable-but-offline configuration on any host.
          command: 'npm start -- --host 127.0.0.1 --port 4184',
          port: 4184,
          healthUrl: 'http://127.0.0.1:4184', readinessTimeoutSeconds: 5, source: 'readme',
        },
      }),
    });
    urlId = updated.urls.find(u => u.label === label)?.id ?? '';
    expect(urlId).not.toBe('');

    await page.goto('/');
    await expect(page.getByTestId('studio-sidebar')).toBeVisible();
    const projectRow = page.locator(`[data-project-name="${project!.displayName}"]`).first();
    await expect(projectRow).toBeVisible();
    const children = projectRow.locator('.studio-tree-children');
    if (!await children.isVisible()) await projectRow.locator('button.tree-row').first().click();
    await page.getByTestId(`studio-explorer-project-url-${project!.displayName}-${urlId}`).click();

    const preview = page.getByTestId('url-preview-tab');
    const failure = page.getByTestId('url-preview-offline');
    await expect(preview).toBeVisible();
    await expect(failure).toHaveAttribute('data-diagnosis', 'not-started');
    await expect(page.getByTestId('url-preview-affected-url')).toContainText('127.0.0.1:4184');
    await expect(page.getByTestId('url-preview-start')).toBeVisible();
    await expect(page.getByTestId('url-preview-open-setup')).toBeVisible();

    for (const [theme, width, height] of [
      ['dark', 1440, 900], ['light', 1440, 900], ['dark', 720, 620], ['light', 720, 620],
    ] as const) {
      await page.setViewportSize({ width, height });
      await setTheme(page, theme);
      const chromeBox = await page.getByTestId('url-preview-addr').boundingBox();
      const failureBox = await failure.boundingBox();
      expect(chromeBox).not.toBeNull();
      expect(failureBox).not.toBeNull();
      expect(failureBox!.y).toBeGreaterThanOrEqual(chromeBox!.y + chromeBox!.height);
      expect(failureBox!.x).toBeGreaterThanOrEqual(0);
      expect(failureBox!.x + failureBox!.width).toBeLessThanOrEqual(width);
      await preview.screenshot({ path: join(shots, `offline-${theme}-${width}.png`) });
    }

    await page.getByTestId('url-preview-details').locator('summary').click();
    await expect(page.getByTestId('url-preview-details')).toHaveAttribute('open', '');
    expect(await failure.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    await preview.screenshot({ path: join(shots, 'offline-light-720-details.png') });

    await page.getByTestId('url-preview-open-setup').click();
    await expect(page.getByTestId('url-preview-quick-setup')).toBeVisible();
    await expect(page.getByTestId('project-urls-add-panel')).toBeVisible();
    await expect(page.getByTestId('project-urls-form-command'))
      .toHaveValue('npm start -- --host 127.0.0.1 --port 4184');
    await expect(page.getByTestId('project-urls-form-provenance')).toContainText('readme');
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.getByTestId('url-preview-quick-setup').screenshot({ path: join(shots, 'quick-setup-light-1440.png') });
  } finally {
    if (urlId) await api(`/api/projects/${project!.id}/urls/${urlId}`, { method: 'DELETE' }).catch(() => undefined);
  }
});
