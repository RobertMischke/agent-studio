import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * Visual proof for the public-demo seed content (AGT-W34 slice S1): the two
 * seeded Wiki trees and the six-Dossier gallery must actually be discoverable
 * in the product, not only in the API. Runs against the isolated per-worktree
 * stack seeded by `scripts/seed-demo-workspace.mjs`, so nothing private can
 * reach a capture.
 */

test.describe.configure({ mode: 'serial' });
test.use({ viewport: { width: 1600, height: 1000 } });

async function shot(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const filePath = testInfo.outputPath(`${name}.png`);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  await page.screenshot({ path: filePath, fullPage: false });
  await testInfo.attach(name, { path: filePath, contentType: 'image/png' });
}

async function captureBoth(page: Page, testInfo: TestInfo, baseName: string): Promise<void> {
  await setTheme(page, 'dark');
  await page.waitForTimeout(250);
  await shot(page, testInfo, `${baseName}--dark--real`);
  await setTheme(page, 'light');
  await page.waitForTimeout(250);
  await shot(page, testInfo, `${baseName}--light--real`);
}

async function dismissBlockingOverlays(page: Page): Promise<void> {
  for (let i = 0; i < 40; i++) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click({ force: true }).catch(() => undefined);
      await page.waitForTimeout(350);
      continue;
    }
    if (i < 4) { await page.waitForTimeout(500); continue; }
    break;
  }
}

test('demo seed: Dossier gallery covers every lifecycle state', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  await page.goto('/#/projects/demo-app/workbenches');
  await dismissBlockingOverlays(page);

  const overview = page.getByTestId('workbench-overview-scope');
  await expect(overview).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('workbench-overview-current-count')).toHaveText('4 current');
  await expect(page.getByTestId('workbench-overview-history-count')).toHaveText('2 history');
  // Decision pending holds DEMO-W2; active holds DEMO-W1 plus the two decided.
  await expect(page.getByTestId('workbench-overview-decision-count')).toHaveText('1');
  await expect(page.getByTestId('workbench-overview-active-count')).toHaveText('3');
  await expect(page.getByTestId('workbench-overview-history-section-count')).toHaveText('2');

  // Documented and Discarded are the two history groups; expanding both makes
  // all six seeded states visible in one capture.
  await page.getByTestId('workbench-overview-completed-toggle').click();
  await page.getByTestId('workbench-overview-discarded-toggle').click();
  await expect(page.getByTestId('workbench-overview-completed-list')).toContainText('Board keyboard shortcuts');
  await expect(page.getByTestId('workbench-overview-discarded-list')).toContainText('Legacy notification banner');
  await page.waitForTimeout(400);
  await captureBoth(page, testInfo, 'demo-seed-dossier-gallery');
});

test('demo seed: a decision-pending Dossier renders its open decisions', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  await page.goto('/#/projects/demo-app/workbenches/export-retention-and-privacy');
  await dismissBlockingOverlays(page);

  const frame = page.getByTestId('workbench-viewer-frame');
  await expect(frame).toBeVisible({ timeout: 60_000 });
  const document = frame.contentFrame();
  await expect(document.getByRole('heading', { name: 'Export retention and privacy' })).toBeVisible({ timeout: 30_000 });
  await expect(document.locator('[data-decision-id]')).toHaveCount(2);
  await page.waitForTimeout(400);
  await captureBoth(page, testInfo, 'demo-seed-dossier-decision-pending');
});

test('demo seed: both Wiki trees are browsable', async ({ page }, testInfo) => {
  test.setTimeout(240_000);
  for (const [slug, landing, folder, deepLink, heading] of [
    ['demo-app', 'Demo App product overview', 'dossiers', 'product/reporting-domain.md', 'Reporting domain'],
    ['demo-platform', 'Demo Platform service overview', 'concepts', 'concepts/request-lifecycle.md', 'Request lifecycle'],
  ] as const) {
    await page.goto(`/#/projects/${slug}/wiki?page=${encodeURIComponent(deepLink)}`);
    // A hash-only change does not remount the shell, so switching projects
    // between iterations needs a real load before the panel resolves.
    await page.reload();
    await dismissBlockingOverlays(page);
    const tree = page.getByTestId('project-wiki-tree');
    await expect(tree).toBeVisible({ timeout: 60_000 });
    await expect(tree).toContainText(landing);
    await expect(tree).toContainText(folder);

    const viewer = page.getByTestId('project-wiki-viewer').first();
    await expect(viewer).toBeVisible({ timeout: 30_000 });
    await expect(viewer).toContainText(heading);
    await expect(viewer).toContainText('Pinned demo data');
    await page.waitForTimeout(400);
    await captureBoth(page, testInfo, `demo-seed-wiki-${slug}`);
  }
});
