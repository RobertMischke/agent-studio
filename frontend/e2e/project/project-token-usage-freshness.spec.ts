import { expect, test } from '../fixtures/dev-backend';
import { mkdirSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const resultsDir = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'token-usage')
  : path.resolve('test-results', 'token-usage');

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test('current receipt, source timestamp, and partial-data warning render in both themes', async ({ page, devBackend }) => {
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok).toBe(true);
  const watchPaths = await pathsResponse.json() as Array<{ name: string; path: string }>;
  const project = watchPaths[0];
  expect(project).toBeTruthy();

  const recordedAt = new Date(Date.now() - 30 * 60 * 1000).toISOString();
  const taskFolder = path.join(project.path, 'tasks', '002', 'AGT-2542');
  mkdirSync(taskFolder, { recursive: true });
  writeFileSync(path.join(taskFolder, 'task.json'), JSON.stringify({
    id: 'AGT-2542',
    title: 'Restore current token analytics',
    state: '3-progress',
    order: 1,
    tokenSummary: {
      calls: 1,
      inputTokens: 24_000_000,
      outputTokens: 880_000,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      totalTokens: 24_880_000,
      allModelsPriced: true,
      lastModel: 'gpt-5.3-codex',
      lastUpdate: recordedAt,
      entries: [{
        ts: recordedAt,
        model: 'gpt-5.3-codex',
        participantId: 'agent:codex',
        inputTokens: 24_000_000,
        outputTokens: 880_000,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        modelPriced: true,
      }],
    },
  }), 'utf8');

  const malformedFolder = path.join(project.path, 'tasks', '002', 'AGT-2599');
  mkdirSync(malformedFolder, { recursive: true });
  writeFileSync(path.join(malformedFolder, 'task.json'), '{ "id": "AGT-2599", "tokenSummary": ', 'utf8');

  await page.goto(`/#/projects/${slugFor(project.name)}/token-usage`);
  await expect(page.getByTestId('project-token-usage-panel')).toBeVisible();
  const leaveRecoveryUncommitted = page.getByRole('button', { name: 'Leave all uncommitted' });
  if (await leaveRecoveryUncommitted.isVisible().catch(() => false)) {
    await leaveRecoveryUncommitted.click();
  }
  await expect(page.getByTestId('token-usage-card-total')).toContainText('24.9M');
  await expect(page.getByTestId('token-usage-card-total')).toContainText('Last 24h');
  await expect(page.getByTestId('token-usage-as-of')).toContainText('Recorded since');
  await expect(page.getByTestId('token-usage-as-of')).toContainText('as of');
  await expect(page.getByTestId('token-usage-source-warning')).toContainText('may be incomplete');
  await expect(page.getByTestId('pipeline-cost-source-warning')).toContainText('may be incomplete');
  await expect(page.getByTestId('pipeline-cost-empty')).toHaveCount(0);
  await expect(page.locator('[data-testid="heatmap-row"][data-job-id="AGT-2542"]')).toBeVisible();

  mkdirSync(resultsDir, { recursive: true });
  await dismissDevErrorDialog(page);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: path.join(resultsDir, `token-usage-freshness-${theme}.png`),
      fullPage: true,
    });
  }
});
