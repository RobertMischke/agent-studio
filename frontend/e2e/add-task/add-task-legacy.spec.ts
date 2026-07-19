import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { setTheme } from '../helpers/theme';

const evidenceDir = process.env['EVIDENCE_DIR'];

test.describe('Add Task dialog legacy guidance', () => {
  test('marks the full-field editor as legacy and opens Orchestrator Chat', async ({ page }, testInfo) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.getByRole('dialog');
    const notice = page.getByTestId('create-dialog-legacy-notice');
    await expect(dialog).toBeVisible();
    await expect(notice).toContainText('Legacy');
    await expect(notice).toContainText('Please use Orchestrator Chat');
    await expect(page.getByTestId('create-in-chat')).toHaveText('Create in chat');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dialog.evaluate((element) => { element.scrollTop = 0; });
      await expect(notice).toBeVisible();

      const border = await notice.evaluate((element) => {
        const style = getComputedStyle(element);
        return {
          leftColor: style.borderLeftColor,
          topColor: style.borderTopColor,
          leftWidth: style.borderLeftWidth,
          topWidth: style.borderTopWidth,
          background: style.backgroundColor,
        };
      });
      expect(border.leftColor).toBe(border.topColor);
      expect(border.leftWidth).toBe(border.topWidth);
      expect(border.background).not.toBe('rgba(0, 0, 0, 0)');

      const screenshot = await dialog.screenshot();
      await testInfo.attach(`new-task-dialog-legacy-${theme}--real.png`, {
        body: screenshot,
        contentType: 'image/png',
      });
      if (evidenceDir) {
        await mkdir(evidenceDir, { recursive: true });
        await dialog.screenshot({ path: join(evidenceDir, `new-task-dialog-legacy-${theme}--real.png`) });
      }
    }

    await page.getByTestId('create-in-chat').click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  });
});
