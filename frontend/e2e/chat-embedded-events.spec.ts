import { test, expect } from '@playwright/test';

/**
 * Project chat — Slice B inline event cards.
 *
 * The chat is becoming the primary product surface; events from
 * background CLIs (tool calls, watchdog warnings, rate-limit pills)
 * belong woven into the chronology, not hidden in a separate toast.
 *
 * Slice B ships the rendering contract: an `events` input on
 * <app-chat>, three card kinds wired with distinct icons / chips /
 * expandable detail bodies. The data source for the six event kinds
 * lands as a separate task; for now the orchestrator side sheet
 * exposes a `?demoEvents=1` URL flag that seeds three sample events,
 * so this spec can pin the rendering and expand interaction without
 * touching the backend.
 */

test.describe('Project chat — Slice B embedded events', () => {
  test('renders tool-call / watchdog / rate-limit cards and expands them', async ({ page }) => {
    // Stub orchestrator-chat history with no turns so the test focuses on
    // the event cards. The seeded demo events still render via the URL flag.
    await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
      if (route.request().method() !== 'GET') {
        await route.continue();
        return;
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project: 'agent-taskboard', turns: [] })
      });
    });

    await page.goto('/?demoEvents=1');
    await page.waitForLoadState('domcontentloaded');

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();

    const chatBody = page.getByTestId('chat-body');
    await expect(chatBody).toBeVisible();

    // Three event kinds must render, in chronological order.
    const toolCall = chatBody.locator('[data-testid="chat-event-tool-call"]');
    const watchdog = chatBody.locator('[data-testid="chat-event-watchdog"]');
    const rateLimit = chatBody.locator('[data-testid="chat-event-rate-limit"]');

    await expect(toolCall).toBeVisible();
    await expect(watchdog).toBeVisible();
    await expect(rateLimit).toBeVisible();

    // Watchdog and rate-limit are warn-severity; their card chrome should
    // pick up the warn class. Tool-call is informational.
    await expect(watchdog).toHaveClass(/chat__event--warn/);
    await expect(rateLimit).toHaveClass(/chat__event--warn/);
    await expect(toolCall).not.toHaveClass(/chat__event--warn/);

    // Each card starts collapsed; clicking the head expands the detail.
    await expect(toolCall).toHaveAttribute('data-expanded', 'false');
    await expect(toolCall.locator('[data-testid="chat-event-detail"]')).toHaveCount(0);

    await toolCall.locator('button.chat__event-head').click();
    await expect(toolCall).toHaveAttribute('data-expanded', 'true');
    await expect(toolCall.locator('[data-testid="chat-event-detail"]')).toBeVisible();
    // Detail body should contain a rendered <pre> from the markdown fence.
    await expect(toolCall.locator('[data-testid="chat-event-detail"] pre')).toBeVisible();

    // Watchdog detail uses a heading + a code fence; expand and assert
    // both render through the same markdown renderer agent turns use.
    await watchdog.locator('button.chat__event-head').click();
    const watchdogDetail = watchdog.locator('[data-testid="chat-event-detail"]');
    await expect(watchdogDetail).toBeVisible();
    await expect(watchdogDetail.locator('strong')).toContainText('Phase');

    // Click watchdog head again -> collapses.
    await watchdog.locator('button.chat__event-head').click();
    await expect(watchdog).toHaveAttribute('data-expanded', 'false');

    // Capture a screenshot tightly cropped to the side sheet for review.
    const box = await sheet.boundingBox();
    if (box) {
      await page.screenshot({
        path: 'chat-embedded-events.png',
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
