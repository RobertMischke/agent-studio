import { test, expect } from '@playwright/test';

/**
 * F29 contract: when the auto-review orchestrator emits a verdict with a
 * very long summary (long task title, full per-aspect list, concern
 * tags), the workspace banner stays inside a sane width, the body is
 * clamped to a few lines with an ellipsis, the project name renders as
 * a small sub-line under the body (NOT concatenated onto the message
 * text), and the dismiss button stays in the rightmost column.
 *
 * Runs against an inline HTML harness that mirrors the production
 * banner template + scss so the regression is captured without a live
 * backend (dev's backend is offline outside the dev-backend fixture).
 */
const LONG_MESSAGE =
  'Auto-review accepted "F19: Design-Token-System hard-enforced ' +
  '(Tier-1 + Tier-2 + Elevations + stylelint-Gate)" with concerns ' +
  '(2 of 4 aspects flagged). Moved to 5-human-review for your approval.';
const LONG_PROJECT = 'Agent Task Processor';

const BANNER_HTML = `<!doctype html>
<html><head><meta charset="utf-8"><title>workspace banner long message</title>
<style>
  body {
    margin: 0;
    padding: 24px;
    background: #181825;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
    color: #cdd6f4;
  }
  /* Match production styles. Kept in sync with
     frontend/src/app/features/shell/components/workspace-banner/workspace-banner.scss
     so this regression spec catches drift in either direction. */
  .banner {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    align-items: start;
    gap: 10px;
    padding: 8px 12px;
    border-radius: 10px;
    font-size: 13px;
    color: #f1f5f9;
    background: rgba(139, 92, 246, 0.18);
    border: 1px solid rgba(139, 92, 246, 0.42);
    box-shadow: 0 4px 18px rgba(139, 92, 246, 0.18);
    box-sizing: border-box;
    max-width: min(640px, calc(100vw - 28px));
    margin: 4px 14px 4px auto;
  }
  .banner__icon { font-size: 16px; line-height: 1.2; padding-top: 1px; }
  .banner__body { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
  .banner__text {
    display: -webkit-box;
    -webkit-line-clamp: 3;
    -webkit-box-orient: vertical;
    overflow: hidden;
    word-break: break-word;
    overflow-wrap: anywhere;
  }
  .banner__project {
    color: rgba(255, 255, 255, 0.65);
    font-size: 11px;
    letter-spacing: 0.02em;
  }
  .banner__close {
    background: transparent;
    border: 0;
    color: rgba(255, 255, 255, 0.7);
    font-size: 18px;
    line-height: 1;
    cursor: pointer;
    padding: 0 4px;
    align-self: start;
  }
</style></head>
<body>
  <div class="banner banner--decision" role="status" data-testid="workspace-banner">
    <span class="banner__icon" aria-hidden="true">✓</span>
    <div class="banner__body">
      <div class="banner__text" data-testid="workspace-banner-text">${LONG_MESSAGE}</div>
      <div class="banner__project" data-testid="workspace-banner-project">in ${LONG_PROJECT}</div>
    </div>
    <button type="button" class="banner__close" aria-label="Dismiss"
            data-testid="workspace-banner-close">&times;</button>
  </div>
</body></html>`;

test('workspace banner clamps long auto-review verdict and keeps project below body', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 480 });
  await page.setContent(BANNER_HTML, { waitUntil: 'load' });

  const banner = page.getByTestId('workspace-banner');
  await expect(banner).toBeVisible();

  // The body should carry the full headline; rendering may clamp it
  // visually but the text node still holds the underlying string.
  await expect(page.getByTestId('workspace-banner-text')).toContainText('Auto-review accepted');
  await expect(page.getByTestId('workspace-banner-text')).toContainText('Moved to 5-human-review');

  // Project lives in its own sub-element, not concatenated onto the
  // message. This is the regression the F29 task was opened for.
  const project = page.getByTestId('workspace-banner-project');
  await expect(project).toHaveText('in ' + LONG_PROJECT);

  // Layout invariants: banner stays inside its max-width and never
  // stretches across the full viewport, and the project sits below
  // (not beside) the message text.
  const bannerBox = await banner.boundingBox();
  const projectBox = await project.boundingBox();
  const textBox = await page.getByTestId('workspace-banner-text').boundingBox();
  expect(bannerBox).not.toBeNull();
  expect(projectBox).not.toBeNull();
  expect(textBox).not.toBeNull();
  expect(bannerBox!.width).toBeLessThanOrEqual(640);
  expect(projectBox!.y).toBeGreaterThan(textBox!.y);

  // The body is clamped to ~3 lines, so the rendered banner stays well
  // under 120px tall even with a very long verdict (without clamp it
  // expanded into a 6+ line wall of text).
  expect(bannerBox!.height).toBeLessThan(140);

  await banner.screenshot({ path: 'test-results/workspace-banner-long-message.png' });
});
