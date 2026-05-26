import { test, expect } from '@playwright/test';
import { listJobs } from '../helpers/jobs';
import { api } from '../helpers/api';

const SHOTS = 'screenshots/chat-sticky-composer';

async function findJobWithOutput(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  for (const j of jobs) {
    try {
      const out = await api<unknown[]>(`/api/jobs/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`);
      if (Array.isArray(out) && out.length > 0) return { id: j.id, watchPath: j.watchPath };
    } catch { /* ignore */ }
  }
  return null;
}

/**
 * Chat layout: sticky composer + no top whitespace.
 *
 * Locks the two fixes from the 2026-05-26 chat-whitespace task:
 *   A) Orchestrator chat body fills available height; composer
 *      stays at the bottom of the side sheet without scrolling.
 *   B) Protocol-pane activity chat-compose sticks to the bottom
 *      of the scroll container.
 */
test.describe('Chat sticky composer', () => {
  test('orchestrator: composer visible at bottom when chat opens', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.mouse.move(0, 0);
    await page.waitForTimeout(500);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();
    await page.waitForTimeout(800);

    const chatBody = page.getByTestId('chat-body');
    await expect(chatBody).toBeVisible();

    const composer = page.getByTestId('chat-input');
    await expect(composer).toBeVisible();

    const sendBtn = page.getByTestId('chat-send');
    await expect(sendBtn).toBeVisible();

    // Composer and send button must be inside the viewport (not scrolled off).
    const vp = page.viewportSize()!;
    const composerBox = await composer.boundingBox();
    expect(composerBox).not.toBeNull();
    expect(composerBox!.y + composerBox!.height).toBeLessThanOrEqual(vp.height + 2);

    const sendBox = await sendBtn.boundingBox();
    expect(sendBox).not.toBeNull();
    expect(sendBox!.y + sendBox!.height).toBeLessThanOrEqual(vp.height + 2);

    await page.screenshot({ path: `${SHOTS}/01-orch-composer-visible.png`, fullPage: false });

    // Chat body should be a constrained scroll container (not growing to
    // full content height). Its bottom edge must be above the composer.
    const bodyBox = await chatBody.boundingBox();
    expect(bodyBox).not.toBeNull();
    expect(bodyBox!.y + bodyBox!.height).toBeLessThan(composerBox!.y + 2);
  });

  test('orchestrator: no excessive whitespace before first message', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.mouse.move(0, 0);
    await page.waitForTimeout(500);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    await page.getByTestId('orch-side-sheet').waitFor({ state: 'visible' });
    await page.waitForTimeout(800);

    const chatBody = page.getByTestId('chat-body');
    await expect(chatBody).toBeVisible();

    // If virtualised mode has a top spacer visible, it should be small
    // (representing off-screen content that is scrolled away). The bug was
    // a 3000+ px spacer visible before any messages.
    const topSpacer = page.getByTestId('chat-spacer-top');
    const spacerVisible = await topSpacer.isVisible().catch(() => false);
    if (spacerVisible) {
      const spacerBox = await topSpacer.boundingBox();
      if (spacerBox) {
        expect(spacerBox.height).toBeLessThan(200);
      }
    }

    // The first chat message or event should be near the top of the chat
    // body, not pushed hundreds of pixels down by a spacer.
    const firstMsg = chatBody.locator('[data-testid^="chat-msg-"], [data-testid^="chat-event-"]').first();
    if (await firstMsg.isVisible()) {
      const bodyBox = await chatBody.boundingBox();
      const msgBox = await firstMsg.boundingBox();
      if (bodyBox && msgBox) {
        const gap = msgBox.y - bodyBox.y;
        expect(gap).toBeLessThan(200);
      }
    }
  });

  test('orchestrator: composer stays visible after scrolling up', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.mouse.move(0, 0);
    await page.waitForTimeout(500);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    await page.getByTestId('orch-side-sheet').waitFor({ state: 'visible' });
    await page.waitForTimeout(800);

    const chatBody = page.getByTestId('chat-body');
    await expect(chatBody).toBeVisible();

    // Scroll the chat body up (if scrollable).
    await chatBody.evaluate((el) => { el.scrollTop = 0; });
    await page.waitForTimeout(200);

    // Composer must still be visible after scrolling. The flex layout keeps
    // it below the chat body; it does not scroll with the messages.
    const composer = page.getByTestId('chat-input');
    await expect(composer).toBeVisible();

    const vp = page.viewportSize()!;
    const composerBox = await composer.boundingBox();
    expect(composerBox).not.toBeNull();
    expect(composerBox!.y + composerBox!.height).toBeLessThanOrEqual(vp.height + 2);

    await page.screenshot({ path: `${SHOTS}/02-orch-scrolled-up-composer-visible.png`, fullPage: false });
  });

  test('activity tab: chat-compose visible alongside activity log', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.waitForLoadState('domcontentloaded');

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const compose = page.getByTestId('activity-chat-compose');
    await expect(compose).toBeVisible({ timeout: 5_000 });

    const vp = page.viewportSize()!;
    const composeBox = await compose.boundingBox();
    expect(composeBox).not.toBeNull();
    expect(composeBox!.y + composeBox!.height).toBeLessThanOrEqual(vp.height + 2);

    await page.screenshot({ path: `${SHOTS}/03-activity-compose-visible.png`, fullPage: false });
  });
});
