import { expect, test } from '@playwright/test';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const reportUrl = pathToFileURL(
  path.resolve(__dirname, '../../../docs/design/app-survey-2026-07-11.html')
).href;

test('app survey filters, area jumps, and card references remain browsable', async ({ page }, testInfo) => {
  page.setDefaultNavigationTimeout(120_000);
  await page.goto(reportUrl, { waitUntil: 'domcontentloaded' });

  const findings = page.locator('main [data-severity]');
  const areaJumps = page.locator('[data-surface-jump]');
  const areaMarkers = page.locator('[data-surface-marker]');
  const cardReferences = page.locator('a[href="#known-agt-2010"]');

  await expect(findings).toHaveCount(137);
  await expect(findings.locator('img')).toHaveCount(137);
  await expect(areaJumps).toHaveCount(12);
  await expect(areaMarkers).toHaveCount(12);
  await expect(cardReferences).toHaveCount(12);

  for (const link of await areaJumps.all()) {
    const href = await link.getAttribute('href');
    expect(href, 'every area jump names an in-page marker').toMatch(/^#surface-/);
    await expect(page.locator(href!)).toHaveCount(1);
  }
  for (const link of await cardReferences.all()) {
    const href = await link.getAttribute('href');
    expect(href, 'every existing-card reference names an in-page explanation').toBe('#known-agt-2010');
    await expect(page.locator(href!)).toHaveCount(1);
  }

  await page.getByRole('button', { name: 'Critical 71' }).click();
  await expect(page.locator('main [data-severity]:visible')).toHaveCount(71);

  await page.getByRole('link', { name: 'Task detail', exact: true }).click();
  const firstVisibleTaskDetail = page.locator('[data-surface="Task detail"]:visible').first();
  await expect(firstVisibleTaskDetail).toBeInViewport();

  const navigationScreenshot = testInfo.outputPath('critical-task-detail.png');
  await page.screenshot({ path: navigationScreenshot });
  await testInfo.attach('critical filter and Task detail jump', {
    path: navigationScreenshot,
    contentType: 'image/png'
  });

  await page.getByRole('button', { name: 'All 137' }).click();
  await cardReferences.first().scrollIntoViewIfNeeded();
  await cardReferences.first().click();
  expect(new URL(page.url()).hash).toBe('#known-agt-2010');
  const crossReferenceTarget = page.locator('#known-agt-2010');
  await crossReferenceTarget.scrollIntoViewIfNeeded();
  await expect(crossReferenceTarget).toBeInViewport();

  const referenceScreenshot = testInfo.outputPath('cross-reference-agt-2010.png');
  await page.screenshot({ path: referenceScreenshot });
  await testInfo.attach('AGT-2010 cross-reference target', {
    path: referenceScreenshot,
    contentType: 'image/png'
  });
});
