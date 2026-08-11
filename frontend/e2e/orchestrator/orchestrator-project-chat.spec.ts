import { expect, test, type Page } from '@playwright/test';
import { installOrchestratorChatBootstrap } from '../helpers/orchestrator-chat-bootstrap';

test.describe('orchestrator project chat', () => {
  test.beforeEach(async ({ page }) => {
    await installOrchestratorChatBootstrap(page, 'Runbook');
  });

  async function expectProjectChatOpen(page: Page) {
    const chat = page.getByTestId('orch-side-sheet');
    await expect(chat).toBeVisible();
    await expect.poll(
      () => chat.evaluate((el) => (el as HTMLElement).offsetWidth),
      { message: 'orchestrator side sheet width' }
    ).toBeGreaterThan(300);
    await expect(chat.getByRole('heading', { name: 'Chat' })).toBeVisible();
    await expect(chat.getByTestId('orch-execution-host')).toBeVisible();
    const projectPicker = chat.getByTestId('orch-side-sheet-project-combo');
    await expect(projectPicker).toBeVisible();
    await expect(projectPicker).toHaveAttribute('placeholder', 'Runbook');
    await expect(chat.getByTestId('chat-context-attachment-add')).toBeVisible();
    await expect(chat.getByPlaceholder(/Ask a question/i)).toBeVisible();

    await expect(chat.getByRole('button', { name: /Search/i })).toHaveCount(0);
    await expect(chat.getByTestId('pchat-search-input')).toHaveCount(0);
    await expect(chat.getByText(/context drawer/i)).toHaveCount(0);
  }

  test('opens the redesigned project chat from the titlebar', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const titlebarChat = page.getByTestId('studio-titlebar-chat');
    await expect(titlebarChat).toBeVisible();
    await titlebarChat.click();

    await expectProjectChatOpen(page);
  });

  test('opens the redesigned project chat from the bottom status bar', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const statusbarChat = page.getByTestId('orch-side-sheet-toggle');
    await expect(statusbarChat).toBeVisible();
    await statusbarChat.click();

    await expectProjectChatOpen(page);
  });
});
