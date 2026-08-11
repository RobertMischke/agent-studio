import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

test.use({ serviceWorkers: 'block' });

const PROJECT = 'context-project';
const SHA = '0123456789abcdef0123456789abcdef01234567';
const FILE = 'src/context.ts';
const RESULTS = resolve(process.env.JOB_RESULTS_DIR ?? 'test-results');

mkdirSync(RESULTS, { recursive: true });

test('adds current diff and known Git sources, sends typed references, and inspects their receipt', async ({ page }) => {
  await seedDiffTab(page, 'dark');
  const posted: unknown[] = [];
  await stubWorkspace(page, posted);

  await page.goto('/');
  await expect(page.getByTestId('studio-diff-view')).toBeVisible();
  await expect(page.getByTestId('studio-diff-render-shell')).toBeVisible();
  await ensureChatOpen(page);
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();

  await page.getByTestId('orch-add-context').click();
  const current = page.getByTestId('orch-context-current-source');
  await expect(current).toContainText(FILE);
  await expect(current).toContainText('L4-L7');
  await current.click();

  const search = page.getByTestId('orch-context-source-search');
  await search.fill('context');
  await expect(page.getByTestId('orch-context-group-files').getByText(FILE)).toBeVisible();
  await page.getByTestId('orch-context-group-files').getByRole('button', { name: /context\.ts/ }).click();
  const commitResults = page.getByTestId('orch-context-group-commits');
  await commitResults.getByRole('button', { name: 'Add commit' }).click();
  await commitResults.getByRole('button', { name: 'Add diff' }).click();
  const explicitContextChips = page.locator('[data-testid^="orch-context-chip-"]');
  await expect(explicitContextChips).toHaveCount(4);
  await page.screenshot({
    path: resolve(RESULTS, 'orchestrator-git-context-picker-dark--mocked.png'),
    fullPage: false,
  });
  await page.getByRole('button', { name: 'Close context picker' }).click();

  await page.getByTestId('chat-input').fill('Explain the selected Git sources');
  await page.getByTestId('chat-send').click();
  await expect.poll(() => posted.length).toBe(1);
  const envelope = (posted[0] as { contextEnvelope: {
    activeSurface: { kind: string; path: string; selection: string[] };
    explicitReferences: Array<{
      kind: string;
      reference: string;
      projectId: string;
      repositoryId: string;
      path?: string;
      lineRanges?: Array<{ startLine: number; endLine: number }>;
    }>;
  } }).contextEnvelope;
  expect(envelope.activeSurface).toMatchObject({ kind: 'diff', path: FILE, selection: ['L4-L7'] });
  expect(envelope.explicitReferences.map(reference => reference.kind))
    .toEqual(['diff', 'repository-file', 'commit', 'diff']);
  expect(envelope.explicitReferences[0]).toMatchObject({
    reference: SHA,
    projectId: PROJECT,
    repositoryId: PROJECT,
    path: FILE,
    lineRanges: [{ startLine: 4, endLine: 7 }],
  });

  const receipt = page.getByTestId('orch-answer-context-receipt');
  await expect(receipt).toBeVisible();
  await page.getByTestId('orch-context-inspect-toggle').click();
  await expect(page.getByTestId('orch-answer-context-source')).toHaveCount(4);
  await expect(page.getByTestId('orch-answer-context-sources')).toContainText(`diff:${PROJECT}/${SHA}:${FILE}#L4-L7`);
  await expect(explicitContextChips).toHaveCount(0);

  await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'light'));
  await page.reload();
  await ensureChatOpen(page);
  const reloadedReceipt = page.getByTestId('orch-answer-context-receipt');
  await expect(reloadedReceipt).toBeVisible();
  await page.getByTestId('orch-context-inspect-toggle').click();
  await expect(page.getByTestId('orch-answer-context-source')).toHaveCount(4);
  await page.screenshot({
    path: resolve(RESULTS, 'orchestrator-git-context-receipt-light--mocked.png'),
    fullPage: false,
  });
});

async function ensureChatOpen(page: Page): Promise<void> {
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  if (await toggle.getAttribute('aria-pressed') !== 'true') await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
}

async function seedDiffTab(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.addInitScript(({ project, sha, theme }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'diff', projectName: project, commitSha: sha }],
      activeKey: `diff:${project}:${sha}`,
    }));
    if (!localStorage.getItem('atp.studio.theme')) localStorage.setItem('atp.studio.theme', theme);
  }, { project: PROJECT, sha: SHA, theme });
}

async function stubWorkspace(page: Page, posted: unknown[]): Promise<void> {
  let sent = false;
  const reply = {
    id: 'reply-1',
    ts: '2026-08-10T12:00:01Z',
    role: 'orchestrator',
    text: 'The selected sources are resolved.',
    contextReceipt: {
      scope: 'project', contextKey: `project:${PROJECT}`, includedBlocks: ['repository-file', 'commit', 'diff'],
      capturedAt: '2026-08-10T12:00:00Z', receiptId: 'receipt-git-1', userTurnId: 'user-1',
      budget: { automaticSoftCapTokens: 4000, automaticHardCapTokens: 6000, totalHardCapTokens: 8000, estimatedIncludedTokens: 430 },
      sources: [
        source(`diff:${PROJECT}/${SHA}:${FILE}#L4-L7`, 'diff', 520, 'excerpted'),
        source(`file:${PROJECT}/${FILE}`, 'repository-file', 240, 'included'),
        source(`commit:${PROJECT}/${SHA}`, 'commit', 640, 'included'),
        source(`diff:${PROJECT}/${SHA}`, 'diff', 320, 'excerpted'),
      ],
    },
  };
  const task = {
    id: 'task-1', taskKey: `${PROJECT}::task-1`, displayKey: 'CTX-4', title: 'Git context fixture',
    state: '3-progress', order: 0, createdAt: '2026-08-10T10:00:00Z', lastActivity: '2026-08-10T11:00:00Z',
    watchPath: `C:/tmp/${PROJECT}`, projectName: PROJECT, folderPath: `C:/tmp/${PROJECT}/task-1`,
    commits: [{ sha: SHA, message: 'Add Git context fixture', filesChanged: 1, at: '2026-08-10T11:00:00Z' }],
    commit: { sha: SHA, message: 'Add Git context fixture', filesChanged: 1, at: '2026-08-10T11:00:00Z' },
  };

  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const body = path === '/api/v1/management/remote-hosts' || path.startsWith('/api/bus/')
      || /\/api\/(tags|workspaces|clients|projects)\/?$/.test(path)
      ? []
      : path === '/api/runner/status' ? { projects: {} }
      : path === '/api/cli/quota' ? { snapshots: [] }
      : path.startsWith('/api/tasks/archive') ? { items: [], total: 0, offset: 0, limit: 50 }
      : {};
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route('**/hubs/**', route => route.abort());
  await page.route(/\/api\/auth\/status$/, route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route(/\/api\/watch-paths$/, route => json(route, [
    { name: PROJECT, path: `C:/tmp/${PROJECT}`, rootPath: `C:/tmp/${PROJECT}`, repositoryPath: `C:/tmp/${PROJECT}` },
  ]));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => json(route, [task]));
  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [task], failedPickup: [],
    autoReview: [], humanReview: [], review: [], completed: [], archive: [],
  }));
  await page.route(new RegExp(`/api/tasks/task-1/commits/${SHA}/files`), route => json(route, {
    sha: SHA, files: [{ path: FILE, status: 'M', added: 1, removed: 1 }],
  }));
  await page.route(new RegExp(`/api/tasks/task-1/commits/${SHA}/diff`), route => json(route, {
    diff: `diff --git a/${FILE} b/${FILE}\n--- a/${FILE}\n+++ b/${FILE}\n@@ -1 +1 @@\n-old\n+new\n`,
  }));
  await page.route(/\/api\/search(?:\?.*)?$/, route => json(route, {
    query: 'context', errors: {}, durationMs: 3, tasks: [],
    files: [{ domain: 'files', projectName: PROJECT, projectColor: '#777777', title: 'context.ts', subtitle: FILE, path: FILE, repositoryId: PROJECT, revision: SHA }],
    commits: [{ domain: 'commits', projectName: PROJECT, projectColor: '#777777', title: 'Add Git context fixture', subtitle: SHA.slice(0, 8), sha: SHA, repositoryId: PROJECT, revision: SHA }],
  }));
  await page.route(/\/api\/orchestrator\/sessions$/, route => json(route, { sessions: [] }));
  await page.route(new RegExp(`/api/orchestrator/context/project:${PROJECT}(?:/refresh)?$`), route => json(route, {
    contextKey: `project:${PROJECT}`, capturedAt: '2026-08-10T12:00:00Z', digest: 'health: ok', sources: [],
  }));
  await page.route(new RegExp(`/api/runner/project:${PROJECT}/orchestrator-chat$`), async route => {
    if (route.request().method() === 'POST') {
      posted.push(route.request().postDataJSON());
      sent = true;
      await json(route, { project: PROJECT, reply });
      return;
    }
    await json(route, {
      project: PROJECT,
      turns: sent ? [{ id: 'user-1', ts: '2026-08-10T12:00:00Z', role: 'user', text: 'Explain the selected Git sources' }, reply] : [],
      executionContext: { executionKind: 'local', hostName: 'local', repoPath: '/workspace', branch: 'main', headSha: SHA, state: 'ready', capturedAt: '2026-08-10T12:00:00Z' },
    });
  });
}

function source(sourceId: string, kind: string, includedCharacters: number, status: string) {
  return {
    sourceId, kind, revision: SHA, sha256: 'a'.repeat(64), freshness: 'immutable-revision',
    includedCharacters, estimatedTokens: Math.ceil(includedCharacters / 4), status,
  };
}

async function json(route: Route, body: unknown): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}
