import { test, expect, Page, Route } from '@playwright/test';

/**
 * Slice E: chat-input directive `/bug <description>`.
 *
 * Posting `/bug <description>` in the project chat should:
 *   1. Hit `POST /api/tasks` with `taskType=bug`, `targetState=0-backlog`,
 *      a meaningful title derived from the first line, and the original
 *      description body with a `Reported via /bug from project chat`
 *      trailer. Hashtag patterns `#tag1 #tag2` at the start of any line
 *      are parsed into the `tags` array.
 *   2. Render a Slice-B inline event card (kind `task`) at the user's
 *      turn position confirming the new bug job, with a click-through
 *      that opens the job detail panel in the same tab.
 *   3. On API error, render the same kind of inline card with
 *      severity=`error` and the error text — never a JS toast.
 *
 * The spec stubs both the orchestrator-chat history endpoint and the
 * `POST /api/tasks` create call so it can run without a live backend.
 */

interface CapturedRequest {
  url: string;
  body: unknown;
  headers: Record<string, string>;
}

async function stubOrchestratorChat(page: Page): Promise<void> {
  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: 'demo-project', turns: [] })
    });
  });
}

async function captureCreateJob(
  page: Page,
  responder: (req: CapturedRequest, route: Route) => Promise<void>
): Promise<{ requests: CapturedRequest[] }> {
  const requests: CapturedRequest[] = [];
  await page.route(/\/api\/tasks(?:\?.*)?$/, async (route) => {
    if (route.request().method() !== 'POST') {
      await route.continue();
      return;
    }
    const headers = route.request().headers();
    const captured: CapturedRequest = {
      url: route.request().url(),
      body: route.request().postDataJSON(),
      headers
    };
    requests.push(captured);
    await responder(captured, route);
  });
  return { requests };
}

async function openProjectChat(page: Page): Promise<void> {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  const sheet = page.getByTestId('orch-side-sheet');
  await expect(sheet).toBeVisible();
  // Wait for the chat input to mount; gracefully skip when the dev
  // backend has no watched projects (the chat surface stays empty).
  const input = page.getByTestId('chat-input');
  if ((await input.count()) === 0) {
    test.skip(true, 'No watched projects available — chat input never mounts');
  }
  await expect(input).toBeVisible({ timeout: 5_000 });
}

test.describe('Project chat — Slice E /bug directive', () => {
  test('files a bug into 0-backlog, parses hashtags, and renders the inline confirmation card', async ({ page }) => {
    await stubOrchestratorChat(page);
    const { requests } = await captureCreateJob(page, async (_req, route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ id: 'fixture-bug-12345' })
      });
    });

    await openProjectChat(page);

    const input = page.getByTestId('chat-input');
    await input.fill(
      '/bug Frontend chips overlap on narrow viewport\n\nRepro: shrink the window below 720px and the kanban tag chips wrap into the next card.\n#performance #frontend'
    );

    const send = page.getByTestId('chat-send');
    await expect(send).toBeEnabled();

    const createCallPromise = page.waitForRequest(
      (req) => req.method() === 'POST' && /\/api\/tasks(?:\?.*)?$/.test(req.url()),
      { timeout: 5_000 }
    );
    await send.click();
    await createCallPromise;

    expect(requests.length).toBe(1);
    const captured = requests[0];
    const body = captured.body as Record<string, unknown>;
    expect(body['taskType']).toBe('bug');
    expect(body['targetState']).toBe('0-backlog');
    expect(typeof body['title']).toBe('string');
    expect((body['title'] as string)).toBe('Frontend chips overlap on narrow viewport');
    const promptMd = body['promptMarkdown'] as string;
    expect(promptMd).toContain('Frontend chips overlap on narrow viewport');
    expect(promptMd).toContain('Reported via /bug from project chat');
    expect(Array.isArray(body['tags'])).toBe(true);
    const tags = body['tags'] as string[];
    expect(tags).toEqual(expect.arrayContaining(['performance', 'frontend']));
    // X-Client-Id header is stamped by the global interceptor; the
    // backend stamps `ownerClientId` from it on the server side, so
    // the request body itself does not need to carry the field.
    expect((captured.headers['x-client-id'] || '').length).toBeGreaterThan(0);

    // Inline event card appears at the user's turn position.
    const card = page.getByTestId('chat-event-task').first();
    await expect(card).toBeVisible({ timeout: 5_000 });
    await expect(card).not.toHaveClass(/chat__event--error/);
    await expect(card.locator('.chat__event-summary')).toContainText('0-backlog');
    await expect(card.locator('.chat__event-summary')).toContainText('Frontend chips overlap on narrow viewport');

    // Expand the card and confirm it cites the new job id.
    await card.locator('button.chat__event-head').click();
    const detail = card.locator('[data-testid="chat-event-detail"]');
    await expect(detail).toBeVisible();
    await expect(detail).toContainText('fixture-bug-12345');
    await expect(detail).toContainText('bug');

    // Click-through: the action button opens the kanban detail panel
    // in the same tab via the existing `?job=...&watchPath=...` URL flow.
    const action = card.locator('[data-testid^="chat-event-action-"]');
    await expect(action).toBeVisible();
    // Screenshot the side sheet with the confirmation card visible so a
    // reviewer can eyeball the rendering (also harvested into the job
    // results folder when JOB_RESULTS_DIR is set).
    const sheet = page.getByTestId('orch-side-sheet');
    const box = await sheet.boundingBox();
    if (box) {
      await page.screenshot({
        path: 'project-chat-bug-success.png',
        clip: {
          x: Math.max(0, box.x - 4),
          y: Math.max(0, box.y - 4),
          width: Math.min(page.viewportSize()!.width - box.x + 4, box.width + 8),
          height: box.height + 8
        }
      });
    }

    await action.click();
    await expect.poll(() => {
      const params = new URL(page.url()).searchParams;
      return params.get('job');
    }, { timeout: 5_000 }).toBe('fixture-bug-12345');
  });

  test('renders an error card with severity=error when POST /api/tasks fails — no JS toast', async ({ page }) => {
    await stubOrchestratorChat(page);
    await captureCreateJob(page, async (_req, route) => {
      await route.fulfill({
        status: 409,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'Job already exists or invalid input' })
      });
    });

    await openProjectChat(page);

    await page.getByTestId('chat-input').fill('/bug Title that backend will reject');
    const send = page.getByTestId('chat-send');
    await expect(send).toBeEnabled();

    const createCallPromise = page.waitForRequest(
      (req) => req.method() === 'POST' && /\/api\/tasks(?:\?.*)?$/.test(req.url()),
      { timeout: 5_000 }
    );
    await send.click();
    await createCallPromise;

    // The failure surfaces in the chat as an event card with severity=error.
    const errorCard = page.locator('[data-testid="chat-event-task"].chat__event--error').first();
    await expect(errorCard).toBeVisible({ timeout: 5_000 });
    await expect(errorCard.locator('.chat__event-summary')).toContainText('Bug not filed');
    await errorCard.locator('button.chat__event-head').click();
    await expect(errorCard.locator('[data-testid="chat-event-detail"]')).toContainText(
      'Job already exists or invalid input'
    );

    // Hard rule: no toast — bug reporting must never feel like a side-channel.
    // The toast surface in this app uses [data-testid^="toast-"]; no such
    // element should appear for a /bug failure.
    expect(await page.locator('[data-testid^="toast-"]').count()).toBe(0);

    const sheet = page.getByTestId('orch-side-sheet');
    const box = await sheet.boundingBox();
    if (box) {
      await page.screenshot({
        path: 'project-chat-bug-error.png',
        clip: {
          x: Math.max(0, box.x - 4),
          y: Math.max(0, box.y - 4),
          width: Math.min(page.viewportSize()!.width - box.x + 4, box.width + 8),
          height: box.height + 8
        }
      });
    }
  });
});
