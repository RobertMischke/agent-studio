import { test, expect } from './fixtures/dev-backend';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from './helpers/api';
import { createJob, getJobDetail } from './helpers/jobs';

/**
 * Orchestrator steering — when the orchestrator cannot pick a path on its
 * own AND identifies a concrete unblocking ask (a screenshot, a choice
 * between options, a missing doc), it now hands back a structured STEER
 * message instead of opaque `BLOCK`. The chat row renders distinctly:
 * question-mark icon, Need / Why labels, option buttons that pre-fill the
 * compose box, and a "Send screenshot" affordance when the Need mentions
 * a screenshot.
 *
 * Both assertions in this spec are filesystem-fixture-driven: we plant a
 * pre-cooked `cli-output.log` containing the steer message and then visit
 * the job. No live CLI; no quota burned.
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

function copyResultsArtifact(absoluteSourcePath: string, jobFolder: string, name: string): void {
  try {
    if (!fs.existsSync(absoluteSourcePath)) return;
    const dst = path.join(jobFolder, 'results');
    fs.mkdirSync(dst, { recursive: true });
    fs.copyFileSync(absoluteSourcePath, path.join(dst, name));
  } catch {
    /* best-effort: results-folder mirroring failure must not fail the spec */
  }
}

function buildSteerLog(): string {
  // Mirrors the persistence shape OrchestratorChatLog.AppendWithStream
  // writes: `[HH:mm:ss.fff] [stream] body`. We seed an agent line, a
  // [[TASK_NEEDS_INPUT]] marker so the chat reads in order, and the
  // orchestrator's STEER handoff in the exact format
  // `OrchestratorReplyParser.FormatSteerForChat` emits.
  const lines = [
    '[09:30:00.000] [stdout] Working on the steering task',
    '[09:30:30.000] [stdout] [[TASK_NEEDS_INPUT: should I keep the legacy modal or migrate to the side sheet?]]',
    '[09:30:45.123] [orchestrator] [steer] [orchestrator] **Need:** screenshot of the affected modal in light theme  **Why:** I cannot tell which variant is broken without seeing the rendered surface  **Options:** A) keep the legacy modal | B) migrate to the side sheet | C) split the difference and side-load the new one'
  ];
  return lines.join('\n') + '\n';
}

test.describe('Orchestrator steering', () => {
  test('renders steer card with Need/Why/Options and wires option clicks + screenshot affordance', async ({ page, devBackend }, testInfo) => {
    // The dev-backend fixture brings dev up if it is offline, so the
    // spec runs without any pre-arranged state. `devBackend` is unused
    // beyond ensuring the lifecycle ran.
    void devBackend;
    const wp = await pickWatchPath();
    const created = await createJob({
      title: `e2e-orchestrator-steering-${Date.now()}`,
      watchPath: wp.path,
      promptMarkdown: 'Pretend NEEDS_INPUT happened so the orchestrator can hand back a steer card.',
      targetState: '3-progress'
    });
    const detail = await getJobDetail(created.id, wp.path);
    const folder = detail.info.folderPath;

    // Plant the steer line in cli-output.log so the activity-log view
    // renders it without spawning a real CLI.
    const logsDir = path.join(folder, 'logs');
    fs.mkdirSync(logsDir, { recursive: true });
    fs.writeFileSync(path.join(logsDir, 'cli-output.log'), buildSteerLog(), 'utf-8');

    // Visit the job and switch to the Activity tab.
    await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);
    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const conversationBtn = page.getByTestId('activity-log-mode-conversation');
    await expect(conversationBtn).toBeVisible({ timeout: 5_000 });
    await conversationBtn.click({ force: true });

    // Steer card is present with the question-mark icon and the parsed
    // Need / Why fields.
    const card = page.getByTestId('orchestrator-steer-card');
    await expect(card).toBeVisible({ timeout: 8_000 });

    const need = page.getByTestId('orchestrator-steer-need');
    await expect(need).toContainText('screenshot of the affected modal');

    const why = page.getByTestId('orchestrator-steer-why');
    await expect(why).toContainText('cannot tell which variant');

    // Capture a "before" screenshot of the steer card while the compose box
    // is still empty. Saved under results/ so reviewers can inspect it.
    const beforePath = testInfo.outputPath('orchestrator-steer-before.png');
    await card.screenshot({ path: beforePath });
    copyResultsArtifact(beforePath, folder, 'orchestrator-steer-before.png');

    // Options render as buttons. Clicking one pre-fills the compose box.
    const optionButtons = page.getByTestId('orchestrator-steer-options').locator('button');
    await expect(optionButtons).toHaveCount(3);
    await expect(optionButtons.nth(0)).toContainText('keep the legacy modal');
    await expect(optionButtons.nth(1)).toContainText('migrate to the side sheet');

    await optionButtons.nth(1).click();

    const composeInput = page.getByTestId('activity-chat-input');
    await expect(composeInput).toHaveValue('migrate to the side sheet');

    // Capture the "after option click" state.
    const afterPath = testInfo.outputPath('orchestrator-steer-after-option.png');
    await page.screenshot({ path: afterPath });
    copyResultsArtifact(afterPath, folder, 'orchestrator-steer-after-option.png');

    // The Need mentions "screenshot", so the upload affordance must be
    // present. Clicking it triggers the hidden file input the parent
    // wires to /api/jobs/{id}/attachments. We assert the affordance
    // exists and click it without supplying a file - we are testing the
    // wiring, not the upload pipe.
    const uploadBtn = page.getByTestId('orchestrator-steer-upload');
    await expect(uploadBtn).toBeVisible();
    const fileInput = page.getByTestId('orchestrator-steer-upload-input');
    await expect(fileInput).toBeAttached();

    let inputClicked = false;
    await page.exposeFunction('__steerInputClicked', () => { inputClicked = true; });
    await page.evaluate(() => {
      const input = document.querySelector('[data-testid="orchestrator-steer-upload-input"]') as HTMLInputElement | null;
      if (!input) return;
      input.addEventListener('click', () => (window as unknown as { __steerInputClicked: () => void }).__steerInputClicked());
    });
    await uploadBtn.click();
    expect(inputClicked).toBe(true);

    // Cleanup: fixture jobs are filtered out of the default board, but we
    // still delete ours so repeated test runs don't leak fixture folders.
    try {
      await api(`/api/jobs/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    } catch {
      /* best-effort */
    }
  });
});
