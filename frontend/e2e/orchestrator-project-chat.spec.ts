import { expect, test } from '@playwright/test';

test.describe('orchestrator project chat', () => {
  test('opens the redesigned project chat without search or context drawer', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const openChat = page.getByTestId('open-orchestrator-project-chat');
    await expect(openChat).toHaveAttribute('title', /Open orchestrator feed/);
    await openChat.click();

    const chat = page.getByTestId('orchestrator-feed');
    await expect(chat).toBeVisible();
    await expect(chat.getByRole('heading', { name: 'Orchestrator' })).toBeVisible();
    await expect(chat.getByText('Runbook · canonical session')).toBeVisible();
    await expect(chat.getByRole('button', { name: /Project/ })).toBeVisible();
    await expect(chat.getByPlaceholder('Ask the project orchestrator...')).toBeVisible();

    await expect(chat.getByRole('button', { name: /Search/i })).toHaveCount(0);
    await expect(chat.getByText(/context drawer/i)).toHaveCount(0);
  });
});
