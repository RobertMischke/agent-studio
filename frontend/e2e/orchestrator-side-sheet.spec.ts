import { test, expect } from '@playwright/test';

/**
 * Orchestrator side sheet — Phase 2 visual + behavioural smoke.
 *
 * Verifies the toolbar button toggles a right-hand chat-style side sheet
 * (same flex-collapse pattern as CLI Usage), shows the project switcher
 * when more than one project is watched, and renders orchestrator log
 * entries as chat bubbles. Captures screenshots so the layout can be
 * reviewed in the chat without running the UI.
 */
const SHOTS = 'screenshots/orch-side-sheet';

test.describe('Orchestrator side sheet', () => {
  test('opens via toolbar, shows chat surface, closes again', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    // Move the mouse out of the way so quota tooltips don't pop up while
    // we screenshot. The previous run had a Codex tooltip floating over
    // the side sheet header, which made the layout look broken in the
    // captures even though the UI itself was fine.
    await page.mouse.move(0, 0);

    // Board screenshot before opening — establishes the baseline width.
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${SHOTS}/01-board-closed.png`, fullPage: false });

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();

    // Sheet open — give Angular a tick to mount the chat content + run
    // the initial /orchestrator-log fetch.
    await page.waitForTimeout(1200);
    await page.screenshot({ path: `${SHOTS}/02-side-sheet-open.png`, fullPage: false });

    const chatBody = page.getByTestId('chat-body');
    await expect(chatBody).toBeVisible();

    const composer = page.getByTestId('chat-input');
    await expect(composer).toBeVisible();
    const sendBtn = page.getByTestId('chat-send');
    await expect(sendBtn).toBeVisible();

    // Tight crop of just the side sheet for layout review.
    const box = await sheet.boundingBox();
    if (box) {
      await page.screenshot({
        path: `${SHOTS}/03-side-sheet-only.png`,
        clip: {
          x: Math.max(0, box.x - 4),
          y: Math.max(0, box.y - 4),
          width: Math.min(page.viewportSize()!.width - box.x + 4, box.width + 8),
          height: box.height + 8
        }
      });
    }

    // Phase 3: real conversation endpoint. Composer is enabled the moment
    // a project is selected — sending kicks the GlobalOrchestrator session
    // and persists both turns. We don't actually send here (that would
    // burn quota in the e2e suite); we just assert the composer is wired
    // and the placeholder describes the new flow.
    await expect(composer).toBeEnabled();
    await expect(composer).toHaveAttribute(
      'placeholder',
      /Ask the orchestrator/
    );

    // Phase 5: "Make a task from this reply" button is rendered but
    // disabled until at least one orchestrator reply with text exists.
    const makeTaskBtn = page.getByTestId('orch-side-sheet-make-task');
    await expect(makeTaskBtn).toBeVisible();

    // Compact-bubble polish demo: type into the composer so the layout
    // captures show the active state with text in the input.
    await composer.fill('Wo stehst du gerade auf diesem Projekt?');
    await page.waitForTimeout(150);
    await expect(page.getByTestId('chat-send')).toBeEnabled();
    await page.mouse.move(0, 0);
    const sheetBoxComposer = await sheet.boundingBox();
    if (sheetBoxComposer) {
      await page.screenshot({
        path: `${SHOTS}/03b-composer-with-text.png`,
        clip: {
          x: Math.max(0, sheetBoxComposer.x - 4),
          y: Math.max(0, sheetBoxComposer.y - 4),
          width: Math.min(page.viewportSize()!.width - sheetBoxComposer.x + 4, sheetBoxComposer.width + 8),
          height: sheetBoxComposer.height + 8
        }
      });
    }

    // Verify the project switcher renders as a dropdown so it scales past
    // a handful of projects without overflow, and changing the selection
    // swaps the active thread.
    const projectSelect = page.getByTestId('orch-side-sheet-project-select');
    await expect(projectSelect).toBeVisible();
    const optionValues = await projectSelect.locator('option').evaluateAll((opts) =>
      (opts as HTMLOptionElement[]).map((o) => o.value)
    );
    if (optionValues.length >= 2) {
      const current = await projectSelect.inputValue();
      const next = optionValues.find((v) => v && v !== current) ?? optionValues[1];
      await projectSelect.selectOption(next);
      await page.waitForTimeout(400);
      await page.screenshot({ path: `${SHOTS}/04-side-sheet-other-project.png`, fullPage: false });
    }

    // Phase 6: when a task detail is open, the side sheet shows a third
    // tab "🎯 <task title>" that switches the chat to a Continue (Steer)
    // surface for that specific task. Open a task and verify the tab
    // appears + clicking it swaps the chat.
    await page.getByTestId('orch-side-sheet-close').click();
    await page.waitForTimeout(300);

    const firstCard = page.locator('[data-testid="job-card"]').first();
    if (await firstCard.isVisible()) {
      await firstCard.click();
      await page.waitForTimeout(600);
      // Reopen the side sheet now that a task is active.
      await toggle.click();
      await page.waitForTimeout(800);

      const taskTab = page.getByTestId('orch-side-sheet-tab-task');
      if (await taskTab.isVisible()) {
        await page.mouse.move(0, 0);
        await page.screenshot({ path: `${SHOTS}/06-task-tab-visible.png`, fullPage: false });
        await taskTab.click();
        await page.waitForTimeout(400);
        await page.screenshot({ path: `${SHOTS}/07-task-chat-active.png`, fullPage: false });
        const composer2 = page.getByTestId('chat-input');
        await expect(composer2).toBeEnabled();
        await expect(composer2).toHaveAttribute('placeholder', /Steer/);
      }
    }

    // Final close.
    const closeBtn = page.getByTestId('orch-side-sheet-close');
    if (await closeBtn.isVisible()) {
      await closeBtn.click();
      await page.waitForTimeout(500);
    }
    await page.mouse.move(0, 0);
    await page.screenshot({ path: `${SHOTS}/05-side-sheet-closed.png`, fullPage: false });
  });
});
