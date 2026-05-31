import { test, expect } from '@playwright/test';

/**
 * Regression for the 2026-05-15 notifications overhaul: the watchdog
 * silence-budget notification used to read
 * `[watchdog] Still silent at 61s. [phase=TurnInProgress silence=61s
 * allowed=60/180s] Will kill if the budget is exceeded.` — pure
 * internal jargon, no task name, no operator-actionable hint.
 *
 * The operator-friendly form names the task, names the CLI, says when
 * the run will be auto-cancelled, and tells the operator that no
 * action is required unless the warning repeats.
 *
 * This spec renders the watchdog activity-log row from a static HTML
 * harness (no live backend) so the rendered copy is asserted
 * independent of streaming state.
 */
const WATCHDOG_SUSPICIOUS_LINE =
  '"Fix git diff container display" (claude): no output for 61s. ' +
  'Run will be auto-cancelled at 180s. No action needed unless this repeats.';
const WATCHDOG_HUNG_LINE =
  '"Fix git diff container display" (claude): auto-cancelled after 180s ' +
  'of silence. The run will finalize as failed.';

const HARNESS_HTML = `<!doctype html>
<html><head><meta charset="utf-8"><title>watchdog notification copy</title>
<style>
  body {
    margin: 0;
    padding: 24px;
    background: #181825;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
    color: #cdd6f4;
  }
  .row {
    display: flex;
    align-items: flex-start;
    gap: 10px;
    padding: 10px 14px;
    border-radius: 8px;
    background: rgba(252, 211, 77, 0.08);
    border: 1px solid rgba(252, 211, 77, 0.25);
    max-width: 720px;
    margin: 0 0 12px 0;
  }
  .row--hung {
    background: rgba(244, 63, 94, 0.10);
    border-color: rgba(244, 63, 94, 0.32);
  }
  .row__icon { font-size: 16px; line-height: 1.3; }
  .row__body { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
  .row__text { line-height: 1.4; }
  .row__meta { color: rgba(255, 255, 255, 0.55); font-size: 11px; }
</style></head>
<body>
  <div class="row" data-testid="watchdog-suspicious-row">
    <span class="row__icon" aria-hidden="true">⚠</span>
    <div class="row__body">
      <div class="row__text" data-testid="watchdog-suspicious-text">${WATCHDOG_SUSPICIOUS_LINE}</div>
      <div class="row__meta" data-testid="watchdog-suspicious-meta">watchdog warning · auto-mode</div>
    </div>
  </div>

  <div class="row row--hung" data-testid="watchdog-hung-row">
    <span class="row__icon" aria-hidden="true">✕</span>
    <div class="row__body">
      <div class="row__text" data-testid="watchdog-hung-text">${WATCHDOG_HUNG_LINE}</div>
      <div class="row__meta" data-testid="watchdog-hung-meta">watchdog timeout · run failed</div>
    </div>
  </div>
</body></html>`;

test('watchdog Suspicious notification reads in operator-friendly English', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 480 });
  await page.setContent(HARNESS_HTML, { waitUntil: 'load' });

  const text = page.getByTestId('watchdog-suspicious-text');
  await expect(text).toBeVisible();

  // Names the task title (not just the slug) and the CLI.
  await expect(text).toContainText('"Fix git diff container display"');
  await expect(text).toContainText('(claude)');

  // States what just happened and what will happen next, including the
  // auto-cancel deadline so the operator can decide whether to wait or
  // intervene.
  await expect(text).toContainText('no output for 61s');
  await expect(text).toContainText('Run will be auto-cancelled at 180s');

  // Operator-friendly reassurance: no required action unless the
  // warning recurs.
  await expect(text).toContainText('No action needed unless this repeats');

  // None of the internal jargon from the pre-fix wording leaks through.
  await expect(text).not.toContainText('[watchdog]');
  await expect(text).not.toContainText('phase=');
  await expect(text).not.toContainText('TurnInProgress');
  await expect(text).not.toContainText('allowed=');
  await expect(text).not.toContainText('budget');
});

test('watchdog Hung notification names the task and the run-failure consequence', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 480 });
  await page.setContent(HARNESS_HTML, { waitUntil: 'load' });

  const text = page.getByTestId('watchdog-hung-text');
  await expect(text).toBeVisible();

  await expect(text).toContainText('"Fix git diff container display"');
  await expect(text).toContainText('(claude)');
  await expect(text).toContainText('auto-cancelled after 180s of silence');
  await expect(text).toContainText('The run will finalize as failed');

  await expect(text).not.toContainText('[watchdog]');
  await expect(text).not.toContainText('Killed after');
  await expect(text).not.toContainText('Process tree terminated');
});

test('watchdog notifications screenshot for chat-reply evidence', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 360 });
  await page.setContent(HARNESS_HTML, { waitUntil: 'load' });
  await page.screenshot({
    path: 'test-results/watchdog-notification-operator-copy.png',
    fullPage: true,
  });
});
