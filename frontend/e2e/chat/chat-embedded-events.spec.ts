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

test.describe('Project chat next-gen semantic events', () => {
  test('projects tool-call, watchdog and rate-limit cards into conversation rows', async ({ page }) => {
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

    const conversation = page.getByTestId('conversation-view');
    await expect(conversation).toBeVisible();

    // Three event kinds must render, in chronological order.
    const toolCall = conversation.getByTestId('conversation-tool-burst').first();
    const watchdog = conversation.getByTestId('conversation-supervisor-wait').first();
    const rateLimit = conversation.locator(
      '[data-testid="conversation-system-status"][data-category="rate-limit"]'
    );

    await expect(toolCall).toBeVisible();
    await expect(watchdog).toBeVisible();
    await expect(rateLimit).toBeVisible();

    await expect(rateLimit).toHaveAttribute('data-severity', 'warn');
    await expect(watchdog).toContainText('silent for 90s');

    const burst = toolCall.getByTestId('tool-burst-chip');
    const burstToggle = burst.getByTestId('tool-burst-row');
    await expect(burstToggle).toHaveAttribute('aria-expanded', 'false');
    await burstToggle.click();
    await expect(burstToggle).toHaveAttribute('aria-expanded', 'true');
    await expect(burst.getByTestId('tool-burst-details')).toBeVisible();

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
