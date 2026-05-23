import { test } from '@playwright/test';

test('diag7 — inspect rendered DOM for prompt-tab-* testids', async ({ page }) => {
  const TASK_JOB_ID = 'diag7-task';
  const TASK_WATCH_PATH = 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard';
  const detail = {
    info: { id: TASK_JOB_ID, jobKey: TASK_WATCH_PATH + '::' + TASK_JOB_ID, title: 'Diag', state: '4-review', order: 0, agent: 'claude', createdAt: new Date().toISOString(), watchPath: TASK_WATCH_PATH, projectName: 'agent-taskboard', folderPath: TASK_WATCH_PATH + '/4-review/' + TASK_JOB_ID, lastActivity: new Date().toISOString(), sessionChain: [], ownerClientId: 'default', commitCount: 0 },
    promptMarkdown: '# Diag', promptHistory: [], statusMarkdown: '# Done', contextUsage: null, log: [], summaryState: { status: 'ready' }
  };
  await page.route(/\/api\/jobs(\?.*)?$/, r => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/jobs/grouped*', r => r.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/watch-paths', r => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ name: 'agent-taskboard', path: TASK_WATCH_PATH }]) }));
  await page.route('**/api/runner/status', r => r.fulfill({ status: 200, contentType: 'application/json', body: '{"projects":{}}' }));
  await page.route('**/api/cli/quota', r => r.fulfill({ status: 200, contentType: 'application/json', body: '{"ttlMs":600000,"snapshots":[]}' }));
  await page.route('**/api/cli/usage', r => r.fulfill({ status: 200, contentType: 'application/json', body: '{"entries":[]}' }));
  await page.route('**/api/clients', r => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));

  const detailRe = new RegExp('/api/jobs/' + TASK_JOB_ID + '(\\?.*)?$');
  await page.route(detailRe, r => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
  await page.route('**/api/jobs/' + TASK_JOB_ID + '/*', r => r.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));

  await page.setViewportSize({ width: 1600, height: 980 });
  await page.goto('http://localhost:4010/?job=' + TASK_JOB_ID + '&watchPath=' + encodeURIComponent(TASK_WATCH_PATH), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3500);
  const promptTabs = await page.evaluate(() => {
    const root = document.querySelector('[data-testid="pane-prompt"]') || document.body;
    return Array.from(root.querySelectorAll('button, [data-testid]')).slice(0, 30).map((el) => ({
      tag: el.tagName,
      testid: el.getAttribute('data-testid'),
      cls: (el as HTMLElement).className?.toString().slice(0, 80),
      text: el.textContent?.trim().slice(0, 40),
    }));
  });
  console.log('PROMPT_TABS:', JSON.stringify(promptTabs));
});
