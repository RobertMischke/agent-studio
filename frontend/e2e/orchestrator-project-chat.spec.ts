import { expect, test } from '@playwright/test';

test.describe('orchestrator project chat', () => {
  test('opens the redesigned project chat without search or context drawer', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const titlebarChat = page.getByTestId('studio-titlebar-chat');
    const statusbarChat = page.getByTestId('orch-side-sheet-toggle');
    if (await titlebarChat.isVisible().catch(() => false)) {
      await titlebarChat.click();
    } else {
      await expect(statusbarChat).toBeVisible();
      await statusbarChat.click();
    }

    const chat = page.getByTestId('orch-side-sheet');
    await expect(chat).toBeVisible();
    await expect(chat.getByRole('heading', { name: 'Orchestrator' })).toBeVisible();
    await expect(chat.getByText('Runbook · canonical session')).toBeVisible();
    await expect(chat.getByRole('button', { name: /Project/ })).toBeVisible();
    await expect(chat.getByTestId('orch-side-sheet-project-combo')).toBeVisible();
    await expect(chat.getByPlaceholder(/Ask the orchestrator/i)).toBeVisible();

    await expect(chat.getByRole('button', { name: /Search/i })).toHaveCount(0);
    await expect(chat.getByTestId('pchat-search-input')).toHaveCount(0);
    await expect(chat.getByText(/context drawer/i)).toHaveCount(0);
  });
});
