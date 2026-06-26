import { type Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';

// On-demand visual documentation screenshot generator. It uses existing task
// data from the configured product workspace and writes output paths relative
// to the frontend/ working dir.

const OUT = '../docs/assets/images/';
const TASK_CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const PRIMARY_TASK_LABELS = ['ASS-1740', 'ASS-847', 'ASS-850', 'ASS-856', 'ASS-1529'];

async function applyVisualCaptureMode(page: Page): Promise<void> {
  if ((process.env.PW_VISUAL_CAPTURE ?? 'marketing') !== 'marketing') return;

  await page.addStyleTag({
    content: `
      body::before,
      .dev-banner,
      [data-testid="dev-banner"] {
        display: none !important;
      }
    `,
  });
}

async function capture(page: Page, fileName: string) {
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${OUT}${fileName}`, fullPage: false });
}

async function openExistingTask(page: Page, preferredLabels: readonly string[]): Promise<string> {
  for (const label of preferredLabels) {
    const card = page.locator(TASK_CARD).filter({ hasText: label }).first();
    if (await card.count()) {
      await card.scrollIntoViewIfNeeded();
      await card.click();
      return label;
    }
  }

  const fallback = page.locator(TASK_CARD).first();
  await expect(fallback).toBeVisible({ timeout: 15_000 });
  const label = (await fallback.innerText()).split('\n').find(Boolean)?.trim() ?? 'first visible task';
  await fallback.click();
  return label;
}

async function clickVisibleTestId(page: Page, testIds: readonly string[]): Promise<void> {
  for (const testId of testIds) {
    const locator = page.getByTestId(testId).first();
    if (await locator.isVisible()) {
      await locator.click();
      return;
    }
  }

  await page.getByTestId(testIds[0]).first().click({ force: true });
}

async function dismissBlockingOverlays(page: Page): Promise<void> {
  // The dev backend can surface a "crash recovery" overlay at startup when it
  // finds uncommitted working-tree changes. It intercepts pointer events and
  // ruins the board shot. Dismiss it non-destructively — "Leave uncommitted"
  // (data-testid="crash-recovery-dismiss") leaves the changes alone.
  for (let i = 0; i < 12; i++) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (!(await dismiss.isVisible().catch(() => false))) break;
    await dismiss.click({ force: true }).catch(() => {});
    await page.waitForTimeout(400);
  }
  // Belt and braces: close any leftover modal overlay so clicks land on the board.
  const overlay = page.getByTestId('studio-overlay-root').first();
  if (await overlay.isVisible().catch(() => false)) {
    await page.keyboard.press('Escape').catch(() => {});
    await page.waitForTimeout(300);
  }
}

test.describe.configure({ mode: 'serial' });

test.use({ viewport: { width: 1440, height: 900 } });

test('readme screenshots — board and task detail states', async ({ page, devBackend }) => {
  void devBackend;
  await api('/api/watch-paths');

  await page.goto('/');
  await applyVisualCaptureMode(page);
  await dismissBlockingOverlays(page);
  await expect(page.getByTestId('dev-banner')).toBeHidden({ timeout: 5_000 });
  await expect(page.locator(TASK_CARD).first()).toBeVisible({ timeout: 15_000 });
  await capture(page, 'board-overview.png');

  await openExistingTask(page, PRIMARY_TASK_LABELS);
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('overview-tab')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-overview.png');

  await page.getByTestId('prompt-tab-description').click();
  await expect(page.getByTestId('files-pane')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-files.png');

  await page.getByTestId('prompt-tab-timeline').click();
  await expect(page.getByTestId('timeline-tab')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-timeline.png');

  await page.getByTestId('prompt-tab-evidence').click();
  await expect(page.getByTestId('evidence-view')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-evidence.png');

  await page.getByTestId('prompt-tab-code-review').click();
  await expect(page.getByTestId('code-review-panel')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-code-review.png');

  await page.getByTestId('prompt-tab-overview').click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-protocol.png');

  await page.getByTestId('inspector-tab-activity').click();
  await expect(page.getByTestId('activity-panel')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-activity.png');

  await clickVisibleTestId(page, ['studio-pane-toggle-git', 'pane-toggle-git']);
  await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 5_000 });
  await capture(page, 'detail-three-panes.png');

  await clickVisibleTestId(page, ['studio-pane-toggle-prompt', 'pane-toggle-prompt']);
  await clickVisibleTestId(page, ['studio-pane-toggle-protocol', 'pane-toggle-protocol']);
  await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 5_000 });
  await capture(page, 'detail-git-focus.png');

  await clickVisibleTestId(page, ['studio-pane-toggle-prompt', 'pane-toggle-prompt']);
  await clickVisibleTestId(page, ['studio-pane-toggle-protocol', 'pane-toggle-protocol']);
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 5_000 });
  const protocolTab = page.getByTestId('inspector-tab-protocol');
  if (await protocolTab.isVisible() && await protocolTab.isEnabled()) {
    await protocolTab.click();
  }
  await capture(page, 'detail-quality-gate.png');
});
