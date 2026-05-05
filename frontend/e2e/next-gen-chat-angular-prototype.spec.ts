import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

const FLAG_KEY = 'atp.flag.nextGenChatPrototype';
const evidenceDir = path.resolve(__dirname, '../../docs/mockups/chat-window-next-gen/evidence');

async function enablePrototype(page: Page): Promise<void> {
  await page.addInitScript((key) => {
    localStorage.setItem(key, '1');
  }, FLAG_KEY);
}

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = route.request().url();
    if (url.includes('/watch-paths')) {
      await route.fulfill({ json: [] });
      return;
    }
    if (url.includes('/jobs/grouped')) {
      await route.fulfill({
        json: { preparation: [], ready: [], progress: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [] }
      });
      return;
    }
    if (url.includes('/runner/status')) {
      await route.fulfill({ json: { projects: {} } });
      return;
    }
    if (url.includes('/cli/quota')) {
      await route.fulfill({ json: { snapshots: [] } });
      return;
    }
    if (url.includes('/cli/usage')) {
      await route.fulfill({ json: { sessions: [], versions: [] } });
      return;
    }
    await route.fulfill({ json: [] });
  });
}

test.describe('@mockup next-gen chat Angular prototype', () => {
  test('captures the interactive Angular workbench prototype', async ({ page }) => {
    await stubApi(page);
    await enablePrototype(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');

    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toBeVisible();
    await expect(page.getByTestId('prototype-detail-chrome')).toContainText('Complete & Next');
    await expect(page.getByTestId('prototype-summary-strip')).toBeVisible();
    await expect(page.getByTestId('prototype-context-pane')).toContainText('Result summary');
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
    await expect(page.getByTestId('prototype-context-pane')).toContainText('Git changes');
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
    await expect(page.getByTestId('prototype-context-pane')).toContainText('Screenshot preview');
    await page.getByTestId('prototype-context-pane').getByRole('button', { name: 'Git split' }).click();
    await expect(page.getByTestId('prototype-lightbox')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-angular-prototype-lightbox.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-lightbox').getByText('Close').click();

    await page.getByTestId('prototype-theme-toggle').click();
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
