import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'model-picker-project';
const TASK_KEY = 'AGT-2163';
const MODELS = [
  { id: 'gpt-5.6-sol', label: 'GPT-5.6 Sol', isDefault: true, available: true,
    thinkingLevels: ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'], defaultThinkingLevel: 'ultra' },
  { id: 'gpt-5.6-pro', label: 'GPT-5.6 Pro', available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh'], defaultThinkingLevel: 'xhigh' },
  { id: 'gpt-5.5', label: 'GPT-5.5', available: true,
    thinkingLevels: ['minimal', 'low', 'medium', 'high', 'xhigh'], defaultThinkingLevel: 'xhigh' },
  { id: 'gpt-5.4', label: 'GPT-5.4', available: true,
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
  { id: 'gpt-5.4-mini', label: 'GPT-5.4 Mini', available: true,
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
  { id: 'gpt-5.3-codex-spark', label: 'GPT-5.3 Codex Spark', available: true,
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
];

async function stubWorkspace(page: Page, sent: Array<Record<string, unknown>>) {
  await page.route(/\/api\//, route => {
    const requestPath = new URL(route.request().url()).pathname;
    let body = '{}';
    if (/\/api\/(?:tags|workspaces|clients|git\/summary|crash-recovery\/pending)\/?$/.test(requestPath)) body = '[]';
    if (requestPath.startsWith('/api/bus/')) body = '[]';
    if (requestPath === '/api/runner/status') body = '{"projects":{}}';
    if (requestPath === '/api/cli/quota') body = '{"snapshots":[]}';
    if (requestPath.startsWith('/api/tasks/archive')) body = '{"items":[],"total":0,"offset":0,"limit":50}';
    if (requestPath.endsWith('/visual-evidence')) body = JSON.stringify({
      project: PROJECT, capturedAt: '2026-07-12T10:00:00Z', unseenCount: 0, items: [],
    });
    if (requestPath.endsWith('/wiki/pulse')) body = JSON.stringify({
      projectName: PROJECT, baseDir: '/tmp/wiki', exists: true, generatedAtUtc: '2026-07-12T10:00:00Z',
      feed: { available: true, reason: null, items: [] },
      inbox: { available: true, reason: null, count: 0, items: [] },
      drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [],
        counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
      critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
    });
    return route.fulfill({ status: 200, contentType: 'application/json', body });
  });
  await page.route(/\/api\/cli\/codex\/models(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ models: MODELS, source: 'live-codex-fixture' }),
  }));
  await page.route(/\/api\/cli\/(?:claude|gemini)\/models(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ models: [], source: 'fixture' }),
  }));
  await page.route(/\/api\/(?:tags|workspaces|clients)\/?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route(/\/api\/workspaces\/?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify([{ id: 'workspace-1',
      displayName: 'Picker workspace', sortOrder: 0, isDefault: true, projects: [{ id: PROJECT,
        displayName: PROJECT, shortCode: 'MP', workspaceId: 'workspace-1', storageLocation: `/tmp/${PROJECT}`,
        archived: false, urls: [{ id: 'preview', label: 'Preview', url: 'https://example.test' }] }] }]),
  }));
  await page.route(/\/api\/watch-paths$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{ name: PROJECT, path: `/tmp/${PROJECT}`, rootPath: `/tmp/${PROJECT}`, repositoryPath: '' }]),
  }));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{ id: 'task-1', taskKey: `${PROJECT}::task-1`, displayKey: TASK_KEY,
      title: 'Picker persistence task', state: '2-ready', projectName: PROJECT, watchPath: `/tmp/${PROJECT}` }]),
  }));
  // The active tab and composer context switch synchronously. Keep the detail
  // request pending so this footer-focused spec does not mount the unrelated,
  // very large task-detail chunk in the dev server.
  await page.route(/\/api\/tasks\/task-1(?:\?.*)?$/, () => undefined);
  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [] }),
  }));
  await page.route(/\/api\/orchestrator\/sessions$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ sessions: [
      { contextKey: `project:${PROJECT}`, kind: 'project', projectId: PROJECT, taskKey: null,
        updatedAt: '2026-07-12T10:00:00Z', model: null, cumulativeInputTokens: 0,
        cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
        runtimeStatus: 'idle', queuePosition: 0 },
      { contextKey: `task:${PROJECT}/${TASK_KEY}`, kind: 'task', projectId: PROJECT, taskKey: TASK_KEY,
        updatedAt: '2026-07-12T10:01:00Z', model: null, cumulativeInputTokens: 0,
        cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
        runtimeStatus: 'idle', queuePosition: 0 },
    ] }),
  }));
  await page.route(/\/api\/runner\/[^/]+(?:\/[^/]+)?\/orchestrator-chat$/, async route => {
    if (route.request().method() === 'POST') {
      sent.push({ ...route.request().postDataJSON(), requestUrl: route.request().url() });
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ project: PROJECT, turns: [] }) });
  });
}

async function choose(page: Page, model: string, reasoning: string) {
  const trigger = page.getByTestId('cac-model-selector-trigger');
  await trigger.click();
  await page.getByTestId(`cac-model-selector-picker-model-${model}`).click();
  await trigger.click();
  await expect(page.getByTestId('cac-model-selector-picker-thinking-pills')).toBeVisible();
  await page.getByTestId(`cac-model-selector-picker-thinking-${reasoning}`).click();
  await expect(trigger).toContainText(reasoning);
}

async function send(page: Page, text: string) {
  await page.getByTestId('chat-input').fill(text);
  await page.getByTestId('chat-send').click();
}

test('full live GPT picker persists across Board and Task contexts', async ({ page }, testInfo) => {
  const sent: Array<Record<string, unknown>> = [];
  await stubWorkspace(page, sent);
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [
      { kind: 'board', projectName: project },
      { kind: 'task', taskKey: `${project}::task-1` },
      { kind: 'hub', projectName: project },
      { kind: 'url-preview', projectName: project, urlId: 'preview' },
    ],
    activeKey: `board:${project}`,
  })), { project: PROJECT });
  await page.goto('/');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await page.getByTestId('orch-side-sheet-toggle').click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await expect(page.getByTestId('chat-toolbar-context')).toHaveText('Board');
  await expect(page.getByTestId('chat-toolbar-routing')).toHaveText('GPT-only · Inherited Codex default');
  await expect(page.getByTestId('orch-side-sheet-draft-actions')).toHaveCount(0);
  await expect(page.getByTestId('orch-side-sheet-make-task')).toHaveCount(0);
  await expect(page.getByTestId('orch-side-sheet-make-task-from-yours')).toHaveCount(0);

  const input = page.getByTestId('chat-input');
  await input.fill('/bug preserved picker draft');
  await page.getByTestId('chat-attach').click();
  await page.locator('input[type="file"]').setInputFiles({
    name: 'picker-proof.png', mimeType: 'image/png', buffer: Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
      'base64'),
  });
  await expect(page.getByTestId('chat-drafts')).toContainText('picker-proof');

  await page.getByTestId('cac-model-selector-trigger').click();
  for (const model of MODELS) {
    await expect(page.getByTestId(`cac-model-selector-picker-model-${model.id}`)).toHaveCount(1);
  }
  await page.getByTestId('cac-model-selector-picker-cancel').click();
  await expect(input).toHaveValue('/bug preserved picker draft');
  await expect(page.getByTestId('chat-drafts')).toContainText('picker-proof');
  await expect(page.getByTestId('cac-model-selector-trigger')).toBeFocused();
  await input.fill('');
  await page.getByTestId('chat-drafts').getByRole('button').click();

  await choose(page, 'gpt-5.6-sol', 'xhigh');
  await expect(page.getByTestId('chat-toolbar-routing')).toHaveText('GPT-only · Operator choice');
  await send(page, 'Board flagship');
  await page.getByTestId(`studio-tab-hub:${PROJECT}`).click();
  await expect(page.getByTestId('chat-toolbar-context')).toHaveText('Deck');
  await expect(page.getByTestId('cac-model-selector-trigger')).toContainText('gpt-5.6-sol');
  await page.getByTestId(`studio-tab-url-preview:${PROJECT}:preview`).click();
  await expect(page.getByTestId('chat-toolbar-context')).toHaveText('URL preview · preview');
  await expect(page.getByTestId('cac-model-selector-trigger')).toContainText('gpt-5.6-sol');
  await page.getByTestId(`studio-tab-task:${PROJECT}::task-1`).click();
  await expect(page.getByTestId('chat-toolbar-context')).toHaveText('Task · AGT-2163');
  await expect(page.getByTestId('cac-model-selector-trigger'))
    .toHaveAttribute('aria-label', /gpt-5\.6-sol.*xhigh/);
  await page.getByTestId('orch-context-badge').click();
  await page.getByTestId(`chat-switcher-row-task:${PROJECT}/${TASK_KEY}`).getByRole('button').first().click();

  await choose(page, 'gpt-5.4-mini', 'low');
  await send(page, 'Task mini');
  await choose(page, 'gpt-5.3-codex-spark', 'high');
  await send(page, 'Task Spark');

  expect(sent).toMatchObject([
    { model: 'gpt-5.6-sol', thinkingLevel: 'xhigh', selectionSource: 'explicit' },
    { model: 'gpt-5.4-mini', thinkingLevel: 'low', selectionSource: 'explicit' },
    { model: 'gpt-5.3-codex-spark', thinkingLevel: 'high', selectionSource: 'explicit' },
  ]);
  expect(sent[0]?.requestUrl).toContain(`/runner/project:${PROJECT}/orchestrator-chat`);
  expect(sent[1]?.requestUrl).toContain(`/runner/task:${PROJECT}/${TASK_KEY}/orchestrator-chat`);
  expect(sent[2]?.requestUrl).toContain(`/runner/task:${PROJECT}/${TASK_KEY}/orchestrator-chat`);

  const results = process.env.JOB_RESULTS_DIR ?? testInfo.outputPath('evidence');
  mkdirSync(results, { recursive: true });
  await page.setViewportSize({ width: 760, height: 900 });
  await page.getByTestId('cac-model-selector-trigger').click();
  await expect(page.getByTestId('cac-model-selector-picker')).toBeVisible();
  await setTheme(page, 'light');
  await page.screenshot({ path: path.join(results, 'orchestrator-model-picker-light-compact.png') });
  await setTheme(page, 'dark');
  await page.screenshot({ path: path.join(results, 'orchestrator-model-picker-dark-compact.png') });
});
