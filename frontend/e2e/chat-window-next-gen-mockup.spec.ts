import { expect, test } from '@playwright/test';
import * as path from 'path';

/**
 * Static screenshot capture for docs/mockups/chat-window-next-gen/ui.html.
 * The mockup is intentionally interactive, so this spec captures the states
 * that define the v6 edge-case contract.
 */
test.describe('@mockup chat-window-next-gen', () => {
  const mockupPath = path.resolve(__dirname, '../../docs/mockups/chat-window-next-gen/ui.html');
  const evidenceDir = path.resolve(__dirname, '../../docs/mockups/chat-window-next-gen/evidence');

  test('captures v6 edge-case states', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('file://' + mockupPath);
    await page.waitForLoadState('domcontentloaded');

    await expect(page.getByTestId('edge-case-cluster')).toBeVisible();
    await expect(page.getByTestId('scenario-note')).toContainText('Tool-heavy logs');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-v6-edge-cases-light.png'),
      fullPage: false
    });

    await page.getByTestId('scenario-wait').click();
    await expect(page.getByTestId('scenario-note')).toContainText('Wait-loop rendering');
    await expect(page.locator('#edge-wait')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-v6-wait-loop.png'),
      fullPage: false
    });

    await page.getByTestId('scenario-images').click();
    await expect(page.locator('#edge-images')).toBeVisible();
    await page.getByTestId('evidence-thumb-2').click();
    await expect(page.getByTestId('image-lightbox')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-v6-image-lightbox.png'),
      fullPage: false
    });
    await page.getByTestId('image-lightbox').getByText('Close').click();

    await page.getByTestId('chat-theme-toggle').click();
    await page.getByTestId('chat-debug-open').click();
    await expect(page.getByTestId('chat-debug-modal')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-v6-debug-dark.png'),
      fullPage: false
    });

    await page.getByTestId('chat-debug-modal').getByText('Close').click();
    await page.getByTestId('chat-theme-toggle').click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-v6-mobile.png'),
      fullPage: false
    });
  });
});
