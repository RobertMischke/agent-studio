import { test, expect, type Page } from '@playwright/test';
import { writeFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Exploratory end-to-end test: create a small task in the Runbook
 * project, watch it move through the lanes, steer via the orchestrator,
 * and complete it. Throughout the run the script writes
 *   - a periodic screenshot   shot-<seq>.png
 *   - a periodic state json   state-<seq>.json
 * into the artifact dir so the operator (or the agent driving this
 * exploratory loop) can watch the UI live without re-running.
 *
 * This is not a pass/fail regression spec. It runs as a probe and
 * captures evidence. Asserts only catch wiring breaks ("could not
 * create a task at all"); the interesting verdict comes from reading
 * the screenshots afterward.
 */

const ARTIFACT_DIR = String.raw`c:\Projects\agent-taskboard-devspace\artifacts\test-runs\20260521-0923-todo-app`;

const TASK_TITLE = 'Playwright probe: tiny TODO sandbox';
const TASK_PROMPT = [
  'You are running inside an automated UI probe. Scope: create exactly ONE small file.',
  '',
  'Working directory restriction: ONLY touch files under `scratch/playwright-probe-todo/`',
  '(create the directory if it does not exist). Do not modify anything else in the repository.',
  '',
  'Goal: write a single self-contained HTML file `index.html` at that path which renders a',
  'minimal to-do list (text input + Add button + ul of items + click to remove). Inline CSS',
  'and JS, no build step, no dependencies. Keep it under 80 lines.',
  '',
  'When done, briefly summarise what you wrote in chat and stop. Do NOT run any tests or',
  'commit anything. The probe will read the file from disk and approve the task in the UI.',
].join('\n');

function ts(): string {
  const d = new Date();
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function logEvent(line: string): void {
  const stamped = `[${ts()}] ${line}\n`;
  appendFileSync(join(ARTIFACT_DIR, 'run.log'), stamped);
}

async function snapshot(page: Page, seq: number, note: string): Promise<void> {
  const shot = join(ARTIFACT_DIR, `shot-${String(seq).padStart(2, '0')}.png`);
  const state = join(ARTIFACT_DIR, `state-${String(seq).padStart(2, '0')}.json`);
  await page.screenshot({ path: shot, fullPage: false });

  // Cheap state snapshot: how many cards per lane (testid-driven), what
  // the active project is, whether the orchestrator rail is open.
  const stateObj = await page.evaluate(() => {
    const railOpen = document.querySelector('app-orchestrator-side-sheet.is-open') !== null;
    const project = document.querySelector(
      '[data-testid="studio-project-picker-trigger"] .studio-pill__label'
    )?.textContent?.trim() ?? null;
    const lanes: Record<string, number> = {};
    document.querySelectorAll('[data-testid^="lane-group-"]').forEach((g) => {
      const name = g.getAttribute('data-testid')!.replace('lane-group-', '');
      const cards = g.querySelectorAll('[data-testid^="job-card-"]').length;
      lanes[name] = cards;
    });
    return {
      railOpen,
      project,
      lanes,
      url: window.location.pathname + window.location.search,
    };
  });
  writeFileSync(state, JSON.stringify({ seq, note, ts: new Date().toISOString(), ...stateObj }, null, 2));
  logEvent(`shot-${String(seq).padStart(2, '0')} ${note}`);
}

test('full lifecycle: create → steer → complete (runbook project)', async ({ page }) => {
  test.setTimeout(15 * 60 * 1000); // 15 min budget — claude can be slow
  mkdirSync(ARTIFACT_DIR, { recursive: true });
  writeFileSync(join(ARTIFACT_DIR, 'run.log'), `=== TODO-app probe ${new Date().toISOString()} ===\n`);

  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });

  let seq = 0;
  await snapshot(page, ++seq, 'app booted, default tab');

  // ----- Pick Runbook project so the new task lands there -----
  await page.getByTestId('studio-project-picker-trigger').click();
  await page.getByTestId('studio-project-picker-Runbook').click();
  await page.waitForTimeout(500);
  await snapshot(page, ++seq, 'picked Runbook project');

  // ----- Open the create-task dialog -----
  // The studio-shell tab-actions surface the "+ Add task" button when
  // the active tab is the board.
  await page.getByTestId('studio-board-add-task').click();
  await expect(page.getByTestId('create-dialog-header')).toBeVisible({ timeout: 5_000 });
  await snapshot(page, ++seq, 'create dialog open');

  // ----- Fill the task -----
  await page.getByTestId('create-title').fill(TASK_TITLE);
  // Watch-path select: pick Runbook explicitly (the project picker only
  // scopes the board view, not the create dialog default).
  const projectSelect = page.getByTestId('create-project-select');
  await projectSelect.selectOption({ label: 'Runbook' }).catch(() => projectSelect.selectOption('Runbook'));
  // Target lane: Ready (so the runner can pick it up immediately).
  await page.getByTestId('create-lane-2-ready').click().catch(() => { /* default lane is fine */ });
  await page.getByTestId('create-prompt').fill(TASK_PROMPT);
  await snapshot(page, ++seq, 'dialog filled');

  // Submit.
  await page.getByRole('button', { name: 'Create' }).click();
  await page.waitForTimeout(800);
  await snapshot(page, ++seq, 'task created, dialog closed');

  // ----- Make sure auto-pickup is on so the runner kicks in -----
  // The titlebar auto-toggle cycles manual ⇄ auto-continuous.
  const autoToggle = page.getByTestId('studio-titlebar-auto-toggle');
  if (await autoToggle.isVisible()) {
    const label = (await autoToggle.locator('.studio-auto-toggle__label').textContent())?.trim().toLowerCase() ?? '';
    if (!label.includes('auto')) {
      await autoToggle.click();
      await page.waitForTimeout(400);
      logEvent(`auto-toggle clicked (was "${label}")`);
    } else {
      logEvent(`auto-toggle already "${label}"`);
    }
  } else {
    logEvent('auto-toggle not visible — runner may still pick up via manual cycle');
  }
  await snapshot(page, ++seq, 'auto pickup armed');

  // ----- Watch the lanes change. Loop with snapshots every 20s. -----
  const deadline = Date.now() + 10 * 60 * 1000; // 10 min for agent work
  let steered = false;
  let completed = false;

  while (Date.now() < deadline) {
    await page.waitForTimeout(20_000);
    await snapshot(page, ++seq, 'lane-poll');

    // At the ~80s mark, send a steer message via the orchestrator.
    if (!steered && seq >= 8) {
      try {
        await page.getByTestId('studio-titlebar-chat').click();
        const rail = page.locator('app-orchestrator-side-sheet');
        await expect(rail).toHaveClass(/is-open/, { timeout: 5_000 });
        await page.waitForTimeout(500);
        const composer = rail.getByTestId('chat-input');
        await composer.fill('Keep the file minimal — under 60 lines is fine. No frameworks.');
        await rail.getByTestId('chat-send').click();
        await page.waitForTimeout(500);
        await snapshot(page, ++seq, 'steered via orchestrator');
        steered = true;
      } catch (e) {
        logEvent(`steer attempt failed: ${(e as Error).message}`);
        steered = true; // don't retry forever
      }
    }

    // Look for the task in the review lane (3-review).
    const reviewCards = page.locator('[data-testid="lane-group-3-active"] [data-testid^="job-card-"]');
    const totalReview = await reviewCards.count();
    if (totalReview > 0) {
      const titles = await reviewCards.allInnerTexts();
      const ourCard = titles.findIndex((t) => t.includes('Playwright probe'));
      if (ourCard >= 0) {
        logEvent(`found our task in 3-active lane (index ${ourCard})`);
        await reviewCards.nth(ourCard).click();
        await page.waitForTimeout(1000);
        await snapshot(page, ++seq, 'opened detail in review');

        const completeBtn = page.getByTestId('studio-task-complete-next');
        if (await completeBtn.isVisible()) {
          await completeBtn.click();
          await page.waitForTimeout(1500);
          await snapshot(page, ++seq, 'clicked Complete');
          completed = true;
          break;
        } else {
          logEvent('complete button not visible — possibly in different sub-state');
          break;
        }
      }
    }
  }

  await snapshot(page, ++seq, completed ? 'COMPLETED' : 'TIMEOUT/end-of-window');
  logEvent(`finished: completed=${completed} seq=${seq}`);
});
