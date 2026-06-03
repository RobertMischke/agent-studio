import { test, expect, Page, Request } from '@playwright/test';

/**
 * F14 regression coverage for the subtle orchestrator-chat context chip,
 * the subtitle-sync fix, and the send-time caching of the navigation
 * context block.
 *
 * The chat backend is stubbed so the spec runs without burning quota.
 * What we lock:
 *   1. The sidesheet subtitle reflects the active picker and updates
 *      when the operator switches project (the 2026-05-22 screenshot
 *      bug was the subtitle staying on "Runbook ..." after switching).
 *   2. The context chip is visible by default with format
 *      `Context: <Project> · Board` and re-renders to
 *      `Context: <Project> · Task '<title>'` when a task is in scope.
 *   3. Clicking the chip's close button hides it and makes the next
 *      send carry `navigationContext: null` (observable in Network).
 *   4. Two identical consecutive sends ship the full context block
 *      only on the first one; the second carries `null` (caching).
 *   5. Switching project clears the cache: the first send on the new
 *      project ships the full block again.
 */

const PROJECT_A = 'project-alpha';
const PROJECT_B = 'project-bravo';
const TASK_ID = 'demo-task-1';
const TASK_TITLE = 'Fix the thing that broke';

interface CapturedRequest {
  body: { navigationContext?: Record<string, unknown> | null; text: string };
  url: string;
}

async function stubChatAndCapture(page: Page): Promise<CapturedRequest[]> {
  const captured: CapturedRequest[] = [];

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    const req: Request = route.request();
    if (req.method() === 'GET') {
      const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(req.url());
      const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project, turns: [] })
      });
      return;
    }
    if (req.method() !== 'POST') {
      await route.continue();
      return;
    }
    const body = req.postDataJSON() as { navigationContext?: Record<string, unknown> | null; text: string };
    captured.push({ body, url: req.url() });
    const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(req.url());
    const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project,
        reply: {
          id: `reply-${Date.now()}-${Math.random()}`,
          ts: new Date().toISOString(),
          role: 'orchestrator',
          text: 'Stubbed orchestrator reply.'
        }
      })
    });
  });

  return captured;
}

async function stubProjectsAndJobs(page: Page) {
  await page.route(/\/api\/watch-paths$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT_A, path: 'C:/tmp/' + PROJECT_A, rootPath: 'C:/tmp/' + PROJECT_A, repositoryPath: '' },
        { name: PROJECT_B, path: 'C:/tmp/' + PROJECT_B, rootPath: 'C:/tmp/' + PROJECT_B, repositoryPath: '' }
      ])
    });
  });
  await page.route(/\/api\/jobs\/grouped(?:\?.*)?$/, async (route) => {
    const emptyLanes = {
      backlog: [], preparation: [], orchestratorPrep: [],
      ready: [], progress: [], failedPickup: [], autoReview: [], humanReview: [],
      review: [], completed: [], archive: []
    };
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        ...emptyLanes,
        autoReview: [
          {
            id: TASK_ID,
            jobKey: PROJECT_A + '::' + TASK_ID,
            title: TASK_TITLE,
            state: '4-auto-review',
            order: 0,
            agent: null,
            cliType: 'claude',
            model: null,
            createdAt: new Date().toISOString(),
            watchPath: 'C:/tmp/' + PROJECT_A,
            projectName: PROJECT_A,
            folderPath: 'C:/tmp/' + PROJECT_A + '/' + TASK_ID,
            execution: null
          }
        ],
        review: [
          {
            id: TASK_ID,
            jobKey: PROJECT_A + '::' + TASK_ID,
            title: TASK_TITLE,
            state: '4-auto-review',
            order: 0,
            agent: null,
            cliType: 'claude',
            model: null,
            createdAt: new Date().toISOString(),
            watchPath: 'C:/tmp/' + PROJECT_A,
            projectName: PROJECT_A,
            folderPath: 'C:/tmp/' + PROJECT_A + '/' + TASK_ID,
            execution: null
          }
        ]
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
  // Give the response handler a tick to settle (so `sending` toggles
  // back to false before the next send).
  await page.waitForTimeout(100);
}

async function selectProject(page: Page, projectName: string) {
  const combo = page.getByTestId('orch-side-sheet-project-combo');
  await combo.click();
  // Use a unique substring (last 5 chars) so the typeahead lands on the
  // intended project even when both fixture names share the same prefix.
  await combo.fill(projectName.slice(-5));
  await page.waitForTimeout(120);
  await combo.press('Enter');
  await page.waitForTimeout(150);
}

test.describe('F14: context chip + subtitle sync + send caching', () => {
  test('subtitle reflects active picker and updates when switching project', async ({ page }) => {
    await stubProjectsAndJobs(page);
    await stubChatAndCapture(page);
    await openSideSheet(page);

    const sheet = page.getByTestId('orch-side-sheet');
    // The subtitle ships as a child <span> of the sidesheet title block.
    const subtitle = sheet.locator('.sidesheet__subtitle');
    await expect(subtitle).toBeVisible();
    // Initial: whichever project came up first should appear with the
    // "canonical session" qualifier.
    const initialText = (await subtitle.textContent())?.trim() ?? '';
    expect(initialText).toMatch(/canonical session$/);
    expect(initialText.startsWith(PROJECT_A) || initialText.startsWith(PROJECT_B)).toBeTruthy();

    // Switch to the OTHER project and assert the subtitle follows.
    const next = initialText.startsWith(PROJECT_A) ? PROJECT_B : PROJECT_A;
    await selectProject(page, next);
    await expect(subtitle).toHaveText(`${next} · canonical session`);
  });

  test('context chip renders board view with project + Board tail', async ({ page }) => {
    // Locks the chip's rendered text against the active picker. The
    // task-detail variant (`Task '<title>'`) is covered by the
    // computed-signal unit test in
    // `orchestrator-side-sheet.context-chip.spec.ts` so this E2E does
    // not need to drive a deep-link into a fixture task.
    await stubProjectsAndJobs(page);
    await stubChatAndCapture(page);

    await openSideSheet(page);
    const chip = page.getByTestId('orch-side-sheet-context-chip');
    await expect(chip).toBeVisible();
    const chipText = page.getByTestId('orch-side-sheet-context-chip-text');
    await expect(chipText).toContainText('Context:');
    await expect(chipText).toContainText('Board');

    // Visual evidence: full-page so the chip is legible alongside the
    // sidesheet header (subtitle) and the composer.
    await page.screenshot({ path: 'screenshots/f14/01-context-chip-board.png', fullPage: false });
  });

  test('dismissing the chip makes the next send carry navigationContext: null', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);
    await openSideSheet(page);

    const chip = page.getByTestId('orch-side-sheet-context-chip');
    await expect(chip).toBeVisible();
    const close = page.getByTestId('orch-side-sheet-context-chip-close');
    await close.click();
    await expect(chip).toBeHidden();

    await sendChat(page, 'no context please');
    expect(captured.length).toBeGreaterThan(0);
    expect(captured[captured.length - 1].body.navigationContext).toBeNull();
  });

  test('consecutive identical sends: first carries full block, second carries null', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);
    await openSideSheet(page);

    await sendChat(page, 'first message');
    expect(captured.length).toBe(1);
    const firstCtx = captured[0].body.navigationContext;
    expect(firstCtx).toBeTruthy();
    expect((firstCtx as Record<string, unknown>).currentPage).toBe('kanban-board');

    await sendChat(page, 'second message');
    expect(captured.length).toBe(2);
    expect(captured[1].body.navigationContext).toBeNull();
  });

  test('switching project re-arms the chip and the next send carries the full block', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);
    await openSideSheet(page);

    // Identify which project the operator landed on, then we'll switch
    // to the other one to drive the picker-change branch.
    const sheet = page.getByTestId('orch-side-sheet');
    const subtitle = sheet.locator('.sidesheet__subtitle');
    const initialProj = ((await subtitle.textContent()) ?? '').replace(' · canonical session', '').trim();
    const otherProj = initialProj === PROJECT_A ? PROJECT_B : PROJECT_A;

    await sendChat(page, 'on project A');
    expect(captured.length).toBe(1);
    expect(captured[0].body.navigationContext).toBeTruthy();

    // Dismiss to demonstrate that switching project also re-arms the
    // chip (the F14 "re-activation" rule).
    await page.getByTestId('orch-side-sheet-context-chip-close').click();
    await expect(page.getByTestId('orch-side-sheet-context-chip')).toBeHidden();

    await selectProject(page, otherProj);
    await expect(page.getByTestId('orch-side-sheet-context-chip')).toBeVisible();

    await sendChat(page, 'on project B');
    expect(captured.length).toBe(2);
    // Picker change clears the cache AND lifts the dismissed flag, so
    // the next send ships the full block again.
    expect(captured[1].body.navigationContext).toBeTruthy();
  });
});
