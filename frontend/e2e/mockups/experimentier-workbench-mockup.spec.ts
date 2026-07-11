import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { pathToFileURL } from 'url';

/**
 * Static interaction and evidence capture for the AGT-2084 concept mockup.
 * No backend or product API is involved.
 */
test.describe('@mockup experimentier-workbench', () => {
  const mockupPath = path.resolve(
    __dirname,
    '../../../docs/concepts/mockups/experimentier-workbench.html'
  );

  test('keeps the experiment readable with scripts disabled', async ({ browser }) => {
    const context = await browser.newContext({ javaScriptEnabled: false });
    const page = await context.newPage();
    await page.goto(pathToFileURL(mockupPath).toString());

    await expect(page.getByRole('heading', { name: 'Project state at a glance' })).toBeVisible();
    await expect(page.locator('#variantTitle')).toHaveText('Micro dashboard dots');
    await expect(page.getByRole('heading', { name: 'Decision panel' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Build as feature' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Archive...' })).toBeVisible();

    await context.close();
  });

  test('covers the experiment, chat, both decisions, themes, and narrow layout', async ({ page }, testInfo) => {
    const configuredEvidenceDir = process.env.AGT2084_RESULTS_DIR?.trim();
    if (configuredEvidenceDir) fs.mkdirSync(configuredEvidenceDir, { recursive: true });
    const evidencePath = (name: string) => configuredEvidenceDir
      ? path.join(configuredEvidenceDir, name)
      : testInfo.outputPath(name);

    const externalRequests: string[] = [];
    page.on('request', request => {
      if (!request.url().startsWith('file:')) externalRequests.push(request.url());
    });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(pathToFileURL(mockupPath).toString());
    await page.waitForLoadState('domcontentloaded');

    await expect(page.getByTestId('workbench-topic-active')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('orchestrator-chat')).toBeVisible();
    await expect(page.locator('#variantTitle')).toHaveText('Micro dashboard dots');
    await expect(page.locator('#prototype')).toHaveAttribute('data-situation', 'active');
    await expect(page.getByText('Sandboxed HTML')).toBeVisible();
    await expect(page.getByText('Trusted host')).toBeVisible();
    await page.screenshot({
      path: evidencePath('experimentier-workbench--overview--mocked.png'),
      fullPage: false
    });

    await page.getByTestId('situation-picker').getByRole('button', { name: 'Escalated' }).click();
    await page.getByTestId('variant-picker').getByRole('button', { name: /Focus chip/ }).click();
    await expect(page.locator('#prototype')).toHaveAttribute('data-situation', 'escalated');
    await expect(page.locator('#variantTitle')).toHaveText('Focus chip');
    await expect(page.locator('.sample-indicator')).toContainText('! 2');

    await page.getByLabel('Message the project orchestrator').fill('Prepare the feature preview for option B.');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#transcript .message').last()).toContainText('Focus chip');
    await expect(page.locator('#transcript .message').last()).toContainText('without your confirmation');

    await page.getByTestId('build-feature').click();
    await expect(page.getByTestId('spawn-preview')).toBeVisible();
    await expect(page.locator('#previewOption')).toHaveText('B · Focus chip');
    await expect(page.locator('#taskPrompt')).toHaveValue(/selected Focus chip signal/);
    await expect(page.locator('#taskPrompt')).toHaveValue(/Source: docs\/workbenches/);
    await page.screenshot({
      path: evidencePath('experimentier-workbench--spawn-preview--mocked.png'),
      fullPage: false
    });

    await page.getByRole('button', { name: 'Simulate feature spawn' }).click();
    await expect(page.locator('#spawnReceipt')).toBeVisible();
    await expect(page.locator('#toast')).toContainText('No real task was created');

    await page.getByRole('button', { name: 'Dark theme' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await page.screenshot({
      path: evidencePath('experimentier-workbench--dark--mocked.png'),
      fullPage: false
    });

    await page.getByTestId('build-feature').click();
    await expect(page.getByTestId('spawn-preview')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('spawn-preview')).toBeHidden();
    await expect(page.locator('#toast')).toBeHidden({ timeout: 7_000 });

    await page.getByTestId('archive-workbench').click();
    await expect(page.getByTestId('archive-preview')).toBeVisible();
    await page.getByRole('button', { name: 'Simulate archive decision' }).click();
    await expect(page.locator('#archiveReason')).toHaveAttribute('aria-invalid', 'true');
    await page.locator('#archiveReason').fill('The compact signal does not add enough value beyond the existing lane badges.');
    await page.screenshot({
      path: evidencePath('experimentier-workbench--archive-preview--mocked.png'),
      fullPage: false
    });
    await page.getByRole('button', { name: 'Simulate archive decision' }).click();
    await expect(page.getByTestId('archive-preview')).toBeHidden();
    await expect(page.locator('#toast')).toContainText('Simulated archive decision');
    await expect(page.locator('#toast')).toBeHidden({ timeout: 7_000 });

    await page.setViewportSize({ width: 390, height: 844 });
    await expect.poll(async () => page.evaluate(() =>
      document.documentElement.scrollWidth <= document.documentElement.clientWidth
    )).toBe(true);
    await expect(page.getByTestId('orchestrator-chat')).toBeVisible();
    await page.screenshot({
      path: evidencePath('experimentier-workbench--narrow--mocked.png'),
      fullPage: false
    });

    expect(externalRequests).toEqual([]);
  });
});
