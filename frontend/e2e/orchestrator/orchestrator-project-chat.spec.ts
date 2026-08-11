import { expect, test, type Page } from '@playwright/test';
import { installOrchestratorChatBootstrap } from '../helpers/orchestrator-chat-bootstrap';

const PROJECT = 'Runbook';

test.describe('orchestrator project chat', () => {
  async function expectProjectChatOpen(page: Page) {
    const chat = page.getByTestId('orch-side-sheet');
    await expect(chat).toBeVisible();
    await expect.poll(
      () => chat.evaluate((el) => (el as HTMLElement).offsetWidth),
      { message: 'orchestrator side sheet width' }
    ).toBeGreaterThan(300);
    await expect(chat).toHaveAttribute('aria-label', 'Chat');
    await expect(chat.getByTestId('orch-panel-header')).toBeVisible();
    await expect(chat.getByTestId('orch-panel-context-type')).toHaveText('Project');
    await expect(chat.getByTestId('orch-panel-context-name')).toHaveText(PROJECT);
    await expect(chat.getByText('Orchestrator', { exact: true })).toHaveCount(0);
    await expect(chat.getByPlaceholder(/Ask a question/i)).toBeVisible();

    await expect(chat.getByRole('button', { name: /Search/i })).toHaveCount(0);
    await expect(chat.getByTestId('pchat-search-input')).toHaveCount(0);
    await expect(chat.getByText(/context drawer/i)).toHaveCount(0);
  }

  test('opens the redesigned project chat from the titlebar', async ({ page }) => {
    await installOrchestratorChatBootstrap(page, PROJECT);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const titlebarChat = page.getByTestId('studio-titlebar-chat');
    await expect(titlebarChat).toBeVisible();
    await titlebarChat.click();

    await expectProjectChatOpen(page);
  });

  test('opens the redesigned project chat from the bottom status bar', async ({ page }) => {
    await installOrchestratorChatBootstrap(page, PROJECT);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const statusbarChat = page.getByTestId('orch-side-sheet-toggle');
    await expect(statusbarChat).toBeVisible();
    await statusbarChat.click();

    await expectProjectChatOpen(page);
  });
});
