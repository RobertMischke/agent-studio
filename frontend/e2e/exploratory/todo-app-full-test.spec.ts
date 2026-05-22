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

// Override at run time via PROBE_ARTIFACT_DIR=... env var; otherwise
// the spec writes into the current "latest" run dir.
const ARTIFACT_DIR = process.env['PROBE_ARTIFACT_DIR']
  ?? String.raw`c:\Projects\agent-taskboard-devspace\artifacts\test-runs\20260522-postfix-probe`;

/**
 * Probes target the dedicated "Playwright Test" project (configured in
 * appsettings.Local.json on both dev and stable). Its sandbox repo at
 * test-repos/playwright-test/ is safe to wipe between runs and never
 * mixes with Runbook / Agent Software Studio.
 */
const PROBE_PROJECT = 'Playwright Test';
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

test('full lifecycle: create → steer → complete (Playwright Test sandbox)', async ({ page }) => {
  test.setTimeout(15 * 60 * 1000); // 15 min budget — claude can be slow
  mkdirSync(ARTIFACT_DIR, { recursive: true });
  writeFileSync(join(ARTIFACT_DIR, 'run.log'), `=== TODO-app probe ${new Date().toISOString()} ===\n`);

  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 15_000 });

  let seq = 0;
  await snapshot(page, ++seq, 'app booted, default tab');

  // ----- Pick the dedicated sandbox project -----
  await page.getByTestId('studio-project-picker-trigger').click();
  await page.getByTestId(`studio-project-picker-item-${PROBE_PROJECT}`).click();
  await page.waitForTimeout(500);
  await snapshot(page, ++seq, `picked ${PROBE_PROJECT} project`);

  // ----- Open the create-task dialog -----
  // The studio-shell tab-actions surface the "+ Add task" button when
  // the active tab is the board.
  await page.getByTestId('studio-board-add-task').click();
  await expect(page.getByTestId('create-dialog-header')).toBeVisible({ timeout: 5_000 });
  await snapshot(page, ++seq, 'create dialog open');

  // ----- Fill the task -----
  await page.getByTestId('create-title').fill(TASK_TITLE);
  // Watch-path select: pick the sandbox project explicitly (the
  // project picker only scopes the board view, not the create dialog
  // default).
  const projectSelect = page.getByTestId('create-project-select');
  await projectSelect.selectOption({ label: PROBE_PROJECT })
    .catch(() => projectSelect.selectOption(PROBE_PROJECT));
  // Target lane: Ready (so the runner can pick it up immediately).
  await page.getByTestId('create-lane-2-ready').click().catch(() => { /* default lane is fine */ });
  await page.getByTestId('create-prompt').fill(TASK_PROMPT);
  await snapshot(page, ++seq, 'dialog filled');

  // Submit. F10 added a stable testid; prefer it over the role lookup.
  await page.getByTestId('create-submit').click();
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

  // ----- Watch the lifecycle. -----
  //
  // G1 (2026-05-22): the old loop polled the UI every 20 s, which is
  // longer than a fast agent run. The 15 s todo-app run completed in
  // one poll window and slipped past every snapshot. Two-track polling:
  //
  //   - Fast track (3 s): hit the API for the task's state. As soon as
  //     it's >= 4-auto-review or any terminal state, break out and act.
  //   - Slow track (every Nth tick ≈ 18 s): take a screenshot for the
  //     post-mortem timeline. Steer message goes out at ~30 s.
  //
  // Using API state as the source of truth (vs. scanning the rendered
  // board) means we no longer rely on the UI's polling tick to refresh
  // the DOM before we look — the backend's state machine moves first.
  const watchPath = await page.evaluate<string | null>(async () => {
    const res = await fetch('/api/jobs/playwright-probe-tiny-todo-sandbox', {
      headers: { 'X-Client-Id': 'local-default' },
    }).catch(() => null);
    if (!res || !res.ok) return null;
    const j = await res.json().catch(() => null);
    return (j as { watchPath?: string } | null)?.watchPath ?? null;
  });
  if (!watchPath) {
    logEvent('WARN: could not resolve watchPath for the probe job — falling back to UI-only scan');
  }
  const fetchState = async (): Promise<string | null> => {
    if (!watchPath) return null;
    const json = await page.evaluate<{ state?: string } | null>(
      async (wp: string) => {
        const url = `/api/jobs/playwright-probe-tiny-todo-sandbox?watchPath=${encodeURIComponent(wp)}`;
        const res = await fetch(url, { headers: { 'X-Client-Id': 'local-default' } }).catch(() => null);
        if (!res || !res.ok) return null;
        return res.json().catch(() => null);
      },
      watchPath,
    );
    return json?.state ?? null;
  };

  const deadline = Date.now() + 10 * 60 * 1000; // 10 min for agent work
  const POLL_MS = 3_000;
  const SNAPSHOT_EVERY = 6; // → snapshot every 18 s
  let steered = false;
  let completed = false;
  let lastObservedState: string | null = null;
  let tick = 0;

  while (Date.now() < deadline) {
    await page.waitForTimeout(POLL_MS);
    tick++;

    const state = await fetchState();
    if (state && state !== lastObservedState) {
      logEvent(`state-change: ${lastObservedState ?? '∅'} → ${state}`);
      lastObservedState = state;
      // On every state transition, also take a screenshot so the
      // post-mortem timeline captures the change visually.
      await snapshot(page, ++seq, `state-change → ${state}`);
    } else if (tick % SNAPSHOT_EVERY === 0) {
      await snapshot(page, ++seq, 'periodic');
    }

    // Steer once, ~30 s in (about tick 10 on the 3 s cadence).
    if (!steered && tick >= 10) {
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

    // Once the state machine reports human-review, find the card in the
    // board and click Complete. The F9 data-states attribute is the
    // canonical lookup for "the lane group containing state X" — see
    // docs/frontend-testids.md.
    if (lastObservedState === '5-human-review') {
      const ourCard = page
        .locator('[data-states*="5-human-review"] [data-testid^="job-card-"]')
        .filter({ hasText: 'Playwright probe' })
        .first();
      if (await ourCard.isVisible().catch(() => false)) {
        logEvent('found our task in 5-human-review lane');
        await ourCard.click();
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
      } else {
        logEvent('state == 5-human-review but card not yet in DOM; will retry next tick');
      }
    }

    // Hard-stop on a terminal-from-our-POV state we don't act on.
    if (lastObservedState === '6-completed' || lastObservedState === '7-archive') {
      logEvent(`state reached ${lastObservedState} externally; exiting watch`);
      break;
    }
  }

  await snapshot(page, ++seq, completed ? 'COMPLETED' : 'TIMEOUT/end-of-window');
  logEvent(`finished: completed=${completed} lastState=${lastObservedState} ticks=${tick}`);
});
