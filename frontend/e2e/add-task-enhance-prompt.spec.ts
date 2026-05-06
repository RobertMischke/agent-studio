import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Add Task dialog - "Enhance" button next to the Prompt field.
 *
 * The button calls POST /api/prompt/enhance, which spawns a Haiku
 * subprocess. To keep this spec deterministic and free, we intercept
 * the request and return a canned response. The dialog should:
 *  - keep the button disabled until the prompt has content
 *  - show a preview pane on success (refined / intent / tags)
 *  - replace the prompt with refined+intent+tags when Apply is clicked
 *  - clear the preview when Discard is clicked
 *  - surface a backend 502 inline without crashing the dialog
 */

const RESULTS_CANDIDATES = [
  'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/roadmap-view/results',
  path.resolve(__dirname, '../test-results')
];

async function ensureResultsDir(): Promise<string> {
  for (const c of RESULTS_CANDIDATES) {
    try {
      await fs.promises.mkdir(c, { recursive: true });
      return c;
    } catch {
      // try next
    }
  }
  return path.resolve(__dirname, '../test-results');
}

test.describe('Add Task - Enhance prompt button', () => {
  test('button is disabled until a prompt is typed', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const enhance = page.getByTestId('create-enhance-prompt');
    await expect(enhance).toBeVisible();
    await expect(enhance).toBeDisabled();

    const prompt = page.getByTestId('create-prompt');
    await prompt.fill('Add a Refine button next to the prompt that calls Haiku and shows a preview.');
    await expect(enhance).toBeEnabled();

    await prompt.fill('     \n   ');
    await expect(enhance).toBeDisabled();
  });

  test('clicking Enhance shows a preview pane and Apply writes the result back', async ({ page }, testInfo) => {
    await page.route('**/api/prompt/enhance', async (route) => {
      const req = route.request().postDataJSON?.();
      expect(typeof req?.prompt).toBe('string');
      expect((req?.prompt ?? '').length).toBeGreaterThan(0);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          refinedPrompt: 'Add an Enhance button to the Create-task dialog that calls Haiku and renders a refined prompt + intent + tag list as a preview pane.',
          intent: 'Add an Enhance button that previews a Haiku-refined prompt',
          tags: ['frontend', 'ui-improvement']
        })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const prompt = page.getByTestId('create-prompt');
    const enhance = page.getByTestId('create-enhance-prompt');

    await prompt.fill(
      'Also, ich hätte gern einen Knopf der mein Prompt aufpoliert und mir die Tags und das Intent dazu liefert.'
    );

    const resultsDir = await ensureResultsDir();
    const beforeBuf = await dialog.screenshot();
    await testInfo.attach('enhance-before', { body: beforeBuf, contentType: 'image/png' });
    await fs.promises.writeFile(path.join(resultsDir, 'enhance-before.png'), beforeBuf);

    await enhance.click();

    const preview = page.getByTestId('create-enhance-preview');
    await expect(preview).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('create-enhance-refined')).toContainText('Enhance button');
    await expect(page.getByTestId('create-enhance-intent')).toContainText('Haiku-refined prompt');
    const tagsBlock = page.getByTestId('create-enhance-tags');
    await expect(tagsBlock).toContainText('frontend');
    await expect(tagsBlock).toContainText('ui-improvement');

    const previewBuf = await dialog.screenshot();
    await testInfo.attach('enhance-preview', { body: previewBuf, contentType: 'image/png' });
    await fs.promises.writeFile(path.join(resultsDir, 'enhance-preview.png'), previewBuf);

    await page.getByTestId('create-enhance-apply').click();

    // Preview disappears, prompt textarea now contains refined + intent + tags.
    await expect(preview).toHaveCount(0);
    const applied = await prompt.inputValue();
    expect(applied).toContain('Add an Enhance button to the Create-task dialog');
    expect(applied).toContain('Intent: Add an Enhance button');
    expect(applied).toContain('Tags: frontend, ui-improvement');

    const appliedBuf = await dialog.screenshot();
    await testInfo.attach('enhance-applied', { body: appliedBuf, contentType: 'image/png' });
    await fs.promises.writeFile(path.join(resultsDir, 'enhance-applied.png'), appliedBuf);
  });

  test('Discard removes the preview without touching the prompt', async ({ page }) => {
    await page.route('**/api/prompt/enhance', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          refinedPrompt: 'Refined version that should NOT replace the prompt.',
          intent: 'Should not appear',
          tags: ['drop-me']
        })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const prompt = page.getByTestId('create-prompt');
    const enhance = page.getByTestId('create-enhance-prompt');
    const original = 'Original prompt the user typed and wants to keep.';
    await prompt.fill(original);

    await enhance.click();
    const preview = page.getByTestId('create-enhance-preview');
    await expect(preview).toBeVisible({ timeout: 5_000 });

    await page.getByTestId('create-enhance-discard').click();
    await expect(preview).toHaveCount(0);
    await expect(prompt).toHaveValue(original);
  });

  test('backend error surfaces inline without crashing the dialog', async ({ page }) => {
    await page.route('**/api/prompt/enhance', async (route) => {
      await route.fulfill({
        status: 502,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title: 'Bad Gateway', detail: 'Haiku refused' })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const prompt = page.getByTestId('create-prompt');
    await prompt.fill('Anything that triggers the backend error path.');

    const enhance = page.getByTestId('create-enhance-prompt');
    await enhance.click();

    const errorBanner = page.getByTestId('create-enhance-error');
    await expect(errorBanner).toBeVisible({ timeout: 5_000 });
    await expect(dialog).toBeVisible();
  });
});
