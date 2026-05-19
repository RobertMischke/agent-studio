import { test, expect, Page } from '@playwright/test';

/**
 * Regression coverage for the "project chat must know its navigation
 * context" fix. The 2026-05-09 incident: operator viewing a task's detail
 * page asked "Was ist der aktuelle Task-Kontext?" in the project chat,
 * the agent had no idea which page the operator was on and produced token
 * soup ("Conversation, Foul Conversation, blablabla").
 *
 * This spec pins:
 *   1. Every project-chat POST carries a `navigationContext` block.
 *   2. When a task detail is open, `currentTaskId`/`Title` are present and
 *      `currentPage` is `task-detail`.
 *   3. When no task is open, `currentPage` is `kanban-board` and the task
 *      fields are absent.
 *   4. The rendered reply contains a real reference to the task (its id /
 *      title) and DOES NOT contain known hallucination signatures.
 *
 * The chat backend's actual LLM call is stubbed so the spec runs without
 * burning quota. The deeper "agent uses the context in the prompt" rule is
 * locked by the backend unit test `OrchestratorChatNavigationContextTests`.
 */

const TASK_ID = 'bug-auto-review-reorder-drops-card';
const TASK_TITLE = 'Bug: reordering a card inside auto-review drops it from the lane';
const PROJECT = 'demo-project';

const HALLUCINATION_SIGNATURES = [
  /Conversation,?\s*Foul Conversation/i,
  /blablabla/i,
  /\b(hello[, ]+){3,}/i,
];

interface CapturedRequest {
  body: unknown;
  headers: Record<string, string>;
}

async function stubChatAndCapture(page: Page) {
  const captured: CapturedRequest[] = [];

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    const req = route.request();
    if (req.method() === 'GET') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project: PROJECT, turns: [] })
      });
      return;
    }
    if (req.method() !== 'POST') {
      await route.continue();
      return;
    }

    const body = req.postDataJSON();
    captured.push({ body, headers: req.headers() });

    // Simulate the context-aware backend: when a task is in scope, the
    // reply mentions it by id; otherwise it asks for clarification. This
    // is the post-fix behaviour the spec exists to defend.
    const nav = (body as { navigationContext?: { currentTaskId?: string; currentTaskTitle?: string } } | null)
      ?.navigationContext;
    const replyText = nav?.currentTaskId
      ? `You are currently viewing **${nav.currentTaskTitle ?? nav.currentTaskId}** (id: \`${nav.currentTaskId}\`). What about it would you like to discuss?`
      : 'No task is currently selected. Which task did you mean?';

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        reply: {
          id: `reply-${Date.now()}`,
          ts: new Date().toISOString(),
          role: 'orchestrator',
          text: replyText
        }
      })
    });
  });

  return captured;
}

async function stubProjectsAndJobs(page: Page) {
  // Watch-paths so the project switcher has at least one project. The
  // host route shapes (jobs, runner status) are stubbed minimally so the
  // app can paint without a live backend.
  await page.route(/\/api\/watch-paths$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        entries: [{ name: PROJECT, path: 'C:/tmp/' + PROJECT, rootPath: 'C:/tmp/' + PROJECT }]
      })
    });
  });
  await page.route(/\/api\/jobs\/grouped(?:\?.*)?$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        states: {
          '4-auto-review': [
            {
              id: TASK_ID,
              jobKey: PROJECT + '::' + TASK_ID,
              title: TASK_TITLE,
              state: '4-auto-review',
              agent: null,
              cliType: 'claude',
              model: null,
              watchPath: 'C:/tmp/' + PROJECT,
              projectName: PROJECT,
              folderPath: 'C:/tmp/' + PROJECT + '/' + TASK_ID,
              execution: null
            }
          ]
        }
      })
    });
  });
  await page.route(/\/api\/jobs(?:\?.*)?$/, async (route) => {
    if (route.request().method() !== 'GET') { await route.continue(); return; }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([])
    });
  });
}

async function openSideSheet(page: Page) {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  const sheet = page.getByTestId('orch-side-sheet');
  await expect(sheet).toBeVisible();
  const input = page.getByTestId('chat-input');
  if ((await input.count()) === 0) {
    test.skip(true, 'No watched projects available - chat input never mounts');
  }
  await expect(input).toBeVisible({ timeout: 5_000 });
}

async function sendChat(page: Page, text: string) {
  await page.getByTestId('chat-input').fill(text);
  const send = page.getByTestId('chat-send');
  await expect(send).toBeEnabled();
  const wait = page.waitForRequest(
    (r) => r.method() === 'POST' && /\/orchestrator-chat$/.test(r.url()),
    { timeout: 5_000 }
  );
  await send.click();
  await wait;
}

test.describe('Project chat context awareness', () => {
  test('sends task-detail navigationContext when a task is open and the reply references the task', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);

    // Open a task detail by URL flow (same path the app uses internally
    // when restoring from a deep link). The detail panel state then
    // flows into the side sheet via the `activeJobId` input.
    await page.goto(`/?job=${encodeURIComponent(TASK_ID)}&watchPath=${encodeURIComponent('C:/tmp/' + PROJECT)}`);
    await page.waitForLoadState('domcontentloaded');

    // Stub the single-job fetch the detail-panel triggers on load.
    await page.route(new RegExp(`/api/jobs/${TASK_ID}(\\?.*)?$`), async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          info: {
            id: TASK_ID,
            jobKey: PROJECT + '::' + TASK_ID,
            title: TASK_TITLE,
            state: '4-auto-review',
            agent: null, cliType: 'claude', model: null,
            watchPath: 'C:/tmp/' + PROJECT,
            projectName: PROJECT,
            folderPath: 'C:/tmp/' + PROJECT + '/' + TASK_ID,
            execution: null
          },
          promptMarkdown: '',
          statusMarkdown: '',
          logTail: ''
        })
      });
    });
    await page.reload();
    await openSideSheet(page);

    await sendChat(page, 'what is the current task?');

    expect(captured.length).toBeGreaterThan(0);
    const body = captured[captured.length - 1].body as { navigationContext?: Record<string, unknown> };
    expect(body.navigationContext).toBeTruthy();
    const nav = body.navigationContext!;
    expect(nav.currentPage).toBe('task-detail');
    expect(nav.currentTaskId).toBe(TASK_ID);
    expect(nav.currentTaskTitle).toBe(TASK_TITLE);
    expect(typeof nav.viewportTimestamp).toBe('string');

    // The rendered reply names the task, NOT token soup.
    const lastBubble = page.locator('[data-testid^="chat-message"]').last();
    await expect(lastBubble).toContainText(TASK_ID, { timeout: 5_000 });
    const bubbleText = (await lastBubble.textContent()) ?? '';
    for (const sig of HALLUCINATION_SIGNATURES) {
      expect(bubbleText).not.toMatch(sig);
    }
  });

  test('sends kanban-board navigationContext when no task is open and the reply asks for clarification', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);

    await openSideSheet(page);
    await sendChat(page, 'what is the current task?');

    expect(captured.length).toBeGreaterThan(0);
    const body = captured[captured.length - 1].body as { navigationContext?: Record<string, unknown> };
    expect(body.navigationContext).toBeTruthy();
    const nav = body.navigationContext!;
    expect(nav.currentPage).toBe('kanban-board');
    expect(nav.currentTaskId).toBeUndefined();
    expect(nav.currentTaskTitle).toBeUndefined();

    const lastBubble = page.locator('[data-testid^="chat-message"]').last();
    await expect(lastBubble).toContainText(/no task|which task/i, { timeout: 5_000 });
    const bubbleText = (await lastBubble.textContent()) ?? '';
    for (const sig of HALLUCINATION_SIGNATURES) {
      expect(bubbleText).not.toMatch(sig);
    }
  });
});
