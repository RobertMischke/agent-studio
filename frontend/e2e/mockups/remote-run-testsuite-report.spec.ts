import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { pathToFileURL } from 'url';

test.describe('@mockup remote-run testsuite report', () => {
  const reportPath = path.resolve(
    __dirname,
    '../../../docs/quality/remote-run-testsuite-report/index.html'
  );

  test('moves from a failing matrix cell to raw evidence in two clicks', async ({ page }) => {
    await page.goto(pathToFileURL(reportPath).toString());

    await expect(page.getByRole('heading', { name: 'Suite summary' })).toBeVisible();
    await expect(page.getByText('2 of 3 runs accepted')).toBeVisible();
    await expect(page.getByText('Telemetry coverage', { exact: true })).toBeVisible();

    const failingCell = page.getByRole('link', { name: 'Fail' }).first();
    await failingCell.click();
    const assertion = page.locator('#assertion-gate-loss-003-gate-result-recovered');
    await expect(assertion).toBeVisible();
    await expect(assertion).toContainText('Transport recovery exhausted');

    const evidenceLink = assertion.getByRole('link', { name: /Open raw evidence/ });
    const evidenceHref = await evidenceLink.getAttribute('href');
    expect(evidenceHref).toContain('gate-loss-003.json#gate-replay');
    await evidenceLink.click();
    await expect(page).toHaveURL(/gate-loss-003\.json#gate-replay$/);
    await expect(page.locator('body')).toContainText('"acknowledged": false');
    await expect(page.locator('body')).toContainText('"terminalClassification": "lost"');
  });

  test('supports disclosures, themes, keyboard use, offline loading, and narrow viewports', async ({ page }, testInfo) => {
    const configuredEvidenceDir = process.env.AGT2398_RESULTS_DIR?.trim();
    if (configuredEvidenceDir) fs.mkdirSync(configuredEvidenceDir, { recursive: true });
    const evidencePath = (name: string) => configuredEvidenceDir
      ? path.join(configuredEvidenceDir, name)
      : testInfo.outputPath(name);
    const externalRequests: string[] = [];
    page.on('request', request => {
      if (!request.url().startsWith('file:')) externalRequests.push(request.url());
    });

    await page.setViewportSize({ width: 1440, height: 960 });
    await page.goto(pathToFileURL(reportPath).toString());
    await page.locator('#run-lease-recovery-002 summary').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('#run-lease-recovery-002')).toHaveAttribute('open', '');
    await expect(page.getByRole('heading', { name: 'Injected incidents and recovery' })).toBeVisible();
    await expect(page.getByRole('link', { name: /daemon-restart.*hardening chronicle/ })).toHaveAttribute(
      'href',
      '../../operations/haertung-verteilte-ausfuehrung/historie.html#incident-zombie-leases'
    );
    await page.screenshot({ path: evidencePath('remote-run-report--light.png'), fullPage: true });

    await page.getByRole('button', { name: 'Switch color theme' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await page.screenshot({ path: evidencePath('remote-run-report--dark.png'), fullPage: false });

    await page.emulateMedia({ media: 'print' });
    await page.evaluate(() => dispatchEvent(new Event('beforeprint')));
    await expect(page.locator('#run-reference-001 .run-body')).toBeVisible();
    await page.evaluate(() => dispatchEvent(new Event('afterprint')));
    await page.emulateMedia({ media: 'screen' });

    await page.setViewportSize({ width: 390, height: 844 });
    await expect.poll(async () => page.evaluate(() =>
      document.documentElement.scrollWidth <= document.documentElement.clientWidth
    )).toBe(true);
    await expect(page.getByRole('heading', { name: 'Acceptance matrix' })).toBeVisible();
    await page.screenshot({ path: evidencePath('remote-run-report--narrow.png'), fullPage: false });

    expect(externalRequests).toEqual([]);
  });

  test('loads the dated AGT-2200 canary package and exposes expected non-delivery', async ({ page }, testInfo) => {
    const configuredReport = process.env.AGT2399_REPORT_PATH?.trim();
    test.skip(!configuredReport, 'Set AGT2399_REPORT_PATH to validate the dated acceptance package.');
    const datedReport = path.resolve(configuredReport!);
    const configuredEvidenceDir = process.env.AGT2399_RESULTS_DIR?.trim();
    if (configuredEvidenceDir) fs.mkdirSync(configuredEvidenceDir, { recursive: true });
    const evidencePath = (name: string) => configuredEvidenceDir
      ? path.join(configuredEvidenceDir, name)
      : testInfo.outputPath(name);
    const externalRequests: string[] = [];
    page.on('request', request => {
      if (!request.url().startsWith('file:')) externalRequests.push(request.url());
    });

    await page.setViewportSize({ width: 1440, height: 960 });
    await page.goto(pathToFileURL(datedReport).toString());
    await expect(page.getByRole('heading', {
      name: 'AGT-2200 remote-run infrastructure acceptance canary'
    })).toBeVisible();
    await expect(page.getByText('12 of 12 runs accepted')).toBeVisible();
    await expect(page.getByText('phase timing 12/12', { exact: false })).toBeVisible();

    const collision = page.locator('#run-canary-worktree-collision');
    await collision.locator('summary').focus();
    await page.keyboard.press('Enter');
    await expect(collision).toHaveAttribute('open', '');
    await expect(collision.getByText('Not published (expected non-delivery)')).toBeVisible();
    await expect(collision.getByRole('link', {
      name: /worktree-collision.*hardening chronicle/
    })).toHaveAttribute(
      'href',
      '../../../operations/haertung-verteilte-ausfuehrung/historie.html#incident-worktree-collision'
    );
    await page.screenshot({
      path: evidencePath('remote-run-canary-2026-07-29--light.png'),
      fullPage: true
    });

    await page.getByRole('button', { name: 'Switch color theme' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await page.screenshot({
      path: evidencePath('remote-run-canary-2026-07-29--dark.png'),
      fullPage: false
    });

    await page.setViewportSize({ width: 390, height: 844 });
    await expect.poll(async () => page.evaluate(() =>
      document.documentElement.scrollWidth <= document.documentElement.clientWidth
    )).toBe(true);
    await page.screenshot({
      path: evidencePath('remote-run-canary-2026-07-29--narrow.png'),
      fullPage: false
    });
    expect(externalRequests).toEqual([]);
  });
});
