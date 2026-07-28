import type { Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { expect, test } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

interface WatchPath {
  path: string;
}

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function setTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate((value) => {
    document.documentElement.dataset['studioTheme'] = value;
    localStorage.setItem('atp.studio.theme', value);
  }, theme);
}

test.describe('Task inspector tab', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`renders Task | Activity | Result and refinement history (${theme})`, async ({ page, devBackend }) => {
      void devBackend;
      const watchPath = await pickWatchPath();
      const job = await createJob({
        title: `task-tab-refinements-${theme}-${Date.now()}`,
        watchPath,
        cliType: 'claude',
        agent: 'claude',
        promptMarkdown: '# Refined operator task\n\nRender this **Markdown** in the calm house style.',
        targetState: '2-ready',
      });

      try {
        await page.route('**/api/crash-recovery/pending', route => route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ pending: [] }),
        }));
        const created = await getJob(job.id, watchPath);
        await writeFile(
          join(created.folderPath, 'prompt-1.md'),
          'Keep the typography quiet and preserve both themes.\n',
        );
        const steeringFolder = join(created.folderPath, 'orchestrator-follow-up-history');
        await mkdir(steeringFolder, { recursive: true });
        await writeFile(
          join(steeringFolder, '20260728-101500-000-review-gap.md'),
          [
            '# Orchestrator steering step',
            '',
            '## Context',
            '- timestamp: 2026-07-28T10:15:00.0000000Z',
            '- cause: review-gap',
            '- reason: Missing visual evidence',
            '',
            '## Steering prompt (verbatim)',
            '',
            'Capture the final light and dark screenshots.',
            '',
          ].join('\n'),
        );

        await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
        await setTheme(page, theme);

        const taskTab = page.getByTestId('inspector-tab-task');
        const activityTab = page.getByTestId('inspector-tab-activity');
        const resultTab = page.getByTestId('inspector-tab-protocol');
        await expect(taskTab).toBeVisible({ timeout: 15_000 });
        await expect(taskTab.locator('xpath=..').getByRole('tab')).toHaveText(['Task', 'Activity', 'Result']);

        await taskTab.click();
        await expect(taskTab).toHaveClass(/pane-tab--active/);
        await expect(page.getByTestId('task-tab-prompt')).toContainText('Refined operator task');
        await expect(page.getByTestId('task-tab-prompt').locator('strong')).toHaveText('Markdown');

        const refinements = page.getByTestId('task-refinement-entry');
        await expect(refinements).toHaveCount(2, { timeout: 10_000 });
        await expect(refinements.nth(0)).toContainText(/Operator|System/);
        await expect(refinements.nth(1)).toContainText(/Operator|System/);
        await expect(page.getByTestId('task-refinement-history')).toContainText('Task extended');
        await expect(page.getByTestId('task-refinement-history')).toContainText('Missing visual evidence');

        await expect(activityTab).toBeVisible();
        await expect(resultTab).toBeVisible();

        const resultsDir = join(process.cwd(), 'results', 'AGT-2408');
        await mkdir(resultsDir, { recursive: true });
        await page.screenshot({
          path: join(resultsDir, `task-tab-refinement-history-${theme}.png`),
          fullPage: false,
        });
      } finally {
        await api(
          `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
          { method: 'DELETE' },
        );
      }
    });
  }
});
