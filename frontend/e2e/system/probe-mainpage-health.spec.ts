import { test } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

test('main page health probe', async ({ page }) => {
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', e => pageErrors.push(`${e.name}: ${e.message}`));

  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto('/', { waitUntil: 'networkidle', timeout: 15000 }).catch(() => {});
  await page.waitForTimeout(2000);

  const outDir = join(process.cwd(), 'test-results', 'mainpage-probe');
  mkdirSync(outDir, { recursive: true });
  await page.screenshot({ path: join(outDir, 'mainpage.png'), fullPage: false });

  const hasAppRoot = await page.locator('app-root').count();
  const hasStudio = await page.locator('[data-studio="root"]').count();
  const bodyText = (await page.locator('body').innerText()).slice(0, 800);

  writeFileSync(join(outDir, 'report.json'), JSON.stringify({
    consoleErrors, pageErrors, hasAppRoot, hasStudio, bodyTextPreview: bodyText
  }, null, 2));
  console.log('REPORT:', { consoleErrorsCount: consoleErrors.length, pageErrorsCount: pageErrors.length, hasAppRoot, hasStudio });
  if (pageErrors.length) console.log('PAGE ERRORS:', pageErrors.slice(0, 5));
  if (consoleErrors.length) console.log('CONSOLE ERRORS:', consoleErrors.slice(0, 10));
});
