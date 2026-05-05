import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

const MOCKUP_BASE_URL = process.env.MOCKUP_BASE_URL ?? 'http://127.0.0.1:4022';
const evidenceDir = path.resolve(__dirname, '../../docs/mockups/chat-window-next-gen/evidence');

async function openPrototype(page: Page): Promise<void> {
  await page.goto(MOCKUP_BASE_URL);
}

test.describe('@mockup next-gen chat Angular prototype', () => {
  test('captures the interactive Angular workbench prototype', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openPrototype(page);

    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toBeVisible();
    await expect(page.getByTestId('prototype-detail-chrome')).toContainText('Complete & Next');
    await expect(page.getByTestId('prototype-topbar-runline')).toContainText('42k tokens');
    await expect(page.getByTestId('prototype-topbar-nav')).toHaveCount(0);
    await expect(page.getByTestId('prototype-summary-strip')).toBeVisible();
    await expect(page.getByTestId('prototype-document-tabs')).toBeVisible();
    await expect(page.getByTestId('prototype-document-result')).toContainText('Summary');
    await expect(page.getByTestId('prototype-document-chat')).toContainText('Task Chat');
    await expect(page.getByTestId('prototype-summary-document')).toContainText('Review ready');
    await expect(page.getByTestId('prototype-pane-result-view')).toContainText('Result summary');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-document-tabs.png'),
      fullPage: false,
    });
    const resultPane = page.getByTestId('prototype-pane-result-view');
    await resultPane.getByRole('button', { name: 'Prompt history' }).click();
    await expect(page.getByTestId('prototype-feature-modal')).toContainText('prompt.md');
    await page.getByTestId('prototype-feature-modal').getByText('Close').click();
    await resultPane.getByRole('button', { name: 'Run timeline' }).click();
    await expect(page.getByTestId('prototype-feature-modal')).toContainText('Run 4');
    await page.getByTestId('prototype-feature-modal').getByText('Close').click();
    await resultPane.getByRole('button', { name: 'Start/Stop' }).click();
    await expect(page.getByTestId('prototype-feature-modal')).toContainText('Run controls');
    await page.getByTestId('prototype-feature-modal').getByText('Close').click();
    await page.waitForTimeout(250);
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-result.png'),
      fullPage: false,
    });

    await page.getByTestId('prototype-topbar-queue').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Queue and automation');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-nav-queue.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-status-popover').getByText('Close').click();

    await page.getByTestId('prototype-status-token').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Token usage heat');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-status-tokens.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-status-popover').getByText('Close').click();

    await page.getByTestId('prototype-status-health').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('System health');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-status-health.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-status-popover').getByText('Close').click();

    await page.getByTestId('prototype-status-session').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Session continuity');
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Worklog');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-status-session.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-status-popover').getByText('Close').click();

    await page.getByTestId('prototype-owner-switch').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Project filter and owner');
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Default route');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-project-owner.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-status-popover').getByText('Close').click();

    await page.getByTestId('prototype-status-model').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('CLI and model');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-status-model.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-status-popover').getByText('Close').click();

    await page.getByTestId('prototype-run-marker').click();
    await expect(page.getByTestId('prototype-run-popover')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-run-popover.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-run-marker').click();

    await page.getByTestId('prototype-rail-guide').click();
    await expect(page.getByTestId('prototype-rail-guide-modal')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-rail-guide.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-rail-guide-modal').getByText('Close').click();

    await page.getByTestId('prototype-pane-git').click();
    await expect(page.getByTestId('prototype-pane-result-view')).toBeVisible();
    await expect(page.getByTestId('prototype-pane-git-view')).toContainText('Git changes');
    await expect(page.getByTestId('prototype-document-git')).toContainText('Git changes');
    await page.getByTestId('prototype-pane-all').click();
    await expect(page.getByTestId('prototype-pane-result-view')).toBeVisible();
    await expect(page.getByTestId('prototype-pane-git-view')).toBeVisible();
    await expect(page.getByTestId('prototype-pane-preview-view')).toBeVisible();
    await expect(page.getByTestId('prototype-pane-debug-view')).toBeVisible();
    await expect(page.getByTestId('prototype-document-debug')).toContainText('Debug trace');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-all-panes.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-pane-preview-close').click();
    await page.getByTestId('prototype-pane-debug-close').click();
    await page.getByTestId('prototype-pane-result-close').click();
    await expect(page.getByTestId('prototype-git-editor')).toContainText('Source editor / diff');
    await page.getByTestId('prototype-topbar-sheet').click();
    await expect(page.getByTestId('prototype-splitter')).toBeVisible();
    await page.getByTestId('prototype-splitter').focus();
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowLeft');
    await expect(page.getByTestId('prototype-splitter')).toHaveAttribute('aria-valuenow', '42');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-git-editor-split.png'),
      fullPage: false,
    });

    await page.getByTestId('prototype-chat-toggle').click();
    await expect(page.getByTestId('prototype-conversation')).toBeHidden();
    await expect(page.getByTestId('prototype-document-chat')).toHaveCount(0);
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-git-no-chat.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-chat-toggle').click();
    await expect(page.getByTestId('prototype-conversation')).toBeVisible();
    await page.getByTestId('prototype-topbar-sheet').click();

    await page.getByTestId('prototype-density-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveAttribute('data-density', 'compact');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-compact.png'),
      fullPage: false,
    });

    await page.getByTestId('prototype-pane-preview').click();
    await expect(page.getByTestId('prototype-pane-preview-view')).toContainText('Screenshot preview');
    await page.getByTestId('prototype-pane-preview-view').getByRole('button', { name: 'Git split' }).click();
    await expect(page.getByTestId('prototype-lightbox')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-lightbox.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-lightbox').getByText('Close').click();

    await page.getByTestId('prototype-density-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveAttribute('data-density', 'comfortable');
    await page.getByTestId('prototype-theme-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveAttribute('data-theme', 'dark');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-dark-workbench.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-pane-debug').click();
    await page.getByTestId('prototype-debug-open').click();
    await expect(page.getByTestId('prototype-debug-modal')).toBeVisible();
    await page.getByTestId('prototype-debug-tab-tokens').click();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-debug-dark.png'),
      fullPage: false,
    });

    await page.getByTestId('prototype-debug-modal').getByText('Close').click();
    await page.getByTestId('prototype-theme-toggle').click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-mobile.png'),
      fullPage: false,
    });
  });
});
