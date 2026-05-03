import { test, expect } from '@playwright/test';

/**
 * Regression: the Add Task dialog must not close on accidental backdrop
 * clicks. The user can lose typed work that way. The dialog now closes
 * only via the explicit X button (top right), the Cancel button, or the
 * Escape key.
 */

const TYPED_TITLE = 'do-not-lose-this-title';

async function openDialog(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.getByRole('button', { name: /add task/i }).first().click();
  const dialog = page.locator('.create-dialog');
  await expect(dialog).toBeVisible();
  await page.locator('.create-dialog input.field__input').first().fill(TYPED_TITLE);
  return dialog;
}

test.describe('Add Task dialog — explicit close only', () => {
  test('clicking the dimmed backdrop does NOT close the dialog and preserves typed input', async ({ page }) => {
    const dialog = await openDialog(page);

    // Click the overlay backdrop somewhere outside the inner panel.
    const overlay = page.locator('.overlay').first();
    await overlay.click({ position: { x: 5, y: 5 } });

    await expect(dialog).toBeVisible();
    await expect(page.locator('.create-dialog input.field__input').first()).toHaveValue(TYPED_TITLE);
  });

  test('Escape closes the dialog', async ({ page }) => {
    const dialog = await openDialog(page);
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
  });

  test('Cancel button closes the dialog', async ({ page }) => {
    const dialog = await openDialog(page);
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(dialog).toBeHidden();
  });

  test('top-right X close button closes the dialog', async ({ page }) => {
    const dialog = await openDialog(page);
    await page.getByTestId('create-dialog-close').click();
    await expect(dialog).toBeHidden();
  });
});
