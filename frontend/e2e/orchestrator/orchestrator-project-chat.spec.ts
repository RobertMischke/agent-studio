import { expect, test } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';

test.describe('orchestrator project chat', () => {
  async function dismissCrashRecovery(page: Page) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (await dismiss.waitFor({ state: 'visible', timeout: 5_000 }).then(() => true).catch(() => false)) {
      await dismiss.click();
      await expect(page.getByTestId('crash-recovery-prompt-overlay')).toBeHidden();
    }
  }

  async function expectProjectChatOpen(page: Page) {
    const chat = page.getByTestId('orch-side-sheet');
    await expect(chat).toBeVisible();
    await expect.poll(
      () => chat.evaluate((el) => (el as HTMLElement).offsetWidth),
      { message: 'orchestrator side sheet width' }
    ).toBeGreaterThan(300);
    await expect(chat.getByRole('heading', { name: 'Chat' })).toBeVisible();
    await expect(chat.getByTestId('orch-side-sheet-project-combo')).toBeVisible();
    await expect(chat.getByPlaceholder(/Ask a question/i)).toBeVisible();
    await expect(chat.getByTestId('chat-context-attachment-add')).toBeVisible();
    await expect(chat.getByTestId('cac-model-selector-trigger')).toBeVisible();
    await expect(chat.getByTestId('chat-send')).toBeVisible();

    await expect(chat.getByRole('button', { name: /Search/i })).toHaveCount(0);
    await expect(chat.getByTestId('pchat-search-input')).toHaveCount(0);
    await expect(chat.getByText(/context drawer/i)).toHaveCount(0);
    await expect(chat.getByTestId('chat-composer-context')).toHaveCount(0);
    await expect(chat.getByTestId('chat-toolbar')).toHaveCount(0);
    await expect(chat.getByTestId('chat-attach')).toHaveCount(0);
  }

  test('opens the redesigned project chat from the titlebar', async ({ page, devBackend: _ }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await dismissCrashRecovery(page);

    const titlebarChat = page.getByTestId('studio-titlebar-chat');
    await expect(titlebarChat).toBeVisible();
    await titlebarChat.click();

    await expectProjectChatOpen(page);
  });

  test('opens the redesigned project chat from the bottom status bar', async ({ page, devBackend: _ }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await dismissCrashRecovery(page);

    const statusbarChat = page.getByTestId('orch-side-sheet-toggle');
    await expect(statusbarChat).toBeVisible();
    await statusbarChat.click();

    await expectProjectChatOpen(page);
  });
});
