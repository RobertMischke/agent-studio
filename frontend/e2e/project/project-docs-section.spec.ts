import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Project-level Security + Architecture sections in the project-detail
 * panel. Verifies that the meta header, file list, ADR list, and viewer
 * render against the real backend, and captures screenshots so the
 * prototype is reviewable from the report.
 */

interface WatchPath { name: string; path: string }

// Outside outputDir so Playwright doesn't wipe these between runs.
const SCREENSHOTS = path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-docs-section');
fs.mkdirSync(SCREENSHOTS, { recursive: true });

test.describe('Project detail — Security & Architecture sections', () => {
  let projectName = '';

  test.beforeAll(async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);
    // Prefer the Agent Software Studio project, since this repo has the
    // canonical docs/operations/security/ and docs/system/architecture/decisions/adr-archive.md files.
    const preferred = paths.find(p => /agent.?software/i.test(p.name)) ?? paths[0];
    projectName = preferred.name;
  });

  test('Security section shows meta + files; Architecture lists ADRs and opens one', async ({ page }) => {
    await page.goto('/');

    await page.getByTestId(`project-shell-open-${projectName}`).click();
    const detail = page.getByTestId('project-detail');
    await expect(detail).toBeVisible({ timeout: 10_000 });

    // -- Security ----------------------------------------------------------
    const sec = page.getByTestId('project-security-section');
    await expect(sec).toBeVisible();
    // Files are listed; we expect at least the seed overview.md.
    await expect(page.getByTestId('project-security-files')).toBeVisible();
    await expect(page.getByTestId('project-security-file-overview.md')).toBeVisible();

    // Open overview.md and assert the rendered Markdown header is shown.
    await page.getByTestId('project-security-file-overview.md').click();
    const viewer = page.getByTestId('project-security-viewer');
    await expect(viewer).toBeVisible();
    await expect(viewer.locator('h1')).toContainText(/security/i, { timeout: 5_000 });

    // -- Architecture ------------------------------------------------------
    const arch = page.getByTestId('project-architecture-section');
    await arch.scrollIntoViewIfNeeded();
    await expect(arch).toBeVisible();
    await expect(page.getByTestId('project-architecture-list')).toBeVisible();
    // The dev repo has ADR-0001 as the first decision.
    const firstAdr = page.getByTestId('project-architecture-ADR-0001');
    await expect(firstAdr).toBeVisible();
    await firstAdr.click();
    await expect(page.getByTestId('project-architecture-viewer')).toBeVisible();

    // Capture both panes for the report.
    await page.screenshot({
      path: `${SCREENSHOTS}/project-docs-overview.png`,
      fullPage: true
    });
  });

  test('Security meta save persists rating + summary across reload', async ({ page }) => {
    await page.goto('/');
    await page.getByTestId(`project-shell-open-${projectName}`).click();
    await expect(page.getByTestId('project-security-section')).toBeVisible({ timeout: 10_000 });

    // Open the meta-edit drawer (a <details> element).
    const editDetails = page.getByTestId('project-security-section').locator('details.psec__meta-edit');
    await editDetails.locator('summary').click();

    const today = new Date().toISOString().slice(0, 10);
    const stamp = `e2e probe ${Date.now()}`;
    await editDetails.locator('input[type="text"]').first().fill(today);
    await editDetails.locator('select').selectOption('Baseline OK');
    await editDetails.locator('textarea').fill(stamp);
    await editDetails.getByRole('button', { name: 'Save' }).click();

    // Wait for the refresh to land the saved values.
    const meta = page.getByTestId('project-security-meta');
    await expect(meta).toContainText(today, { timeout: 5_000 });
    await expect(meta).toContainText(stamp);
    await expect(page.getByTestId('project-security-rating')).toHaveText('Baseline OK');

    // Reload to confirm server-side persistence.
    await page.reload();
    await page.getByTestId(`project-shell-open-${projectName}`).click();
    await expect(page.getByTestId('project-security-meta')).toContainText(stamp, { timeout: 10_000 });

    // Scroll the security section into view so the screenshot actually
    // captures the saved meta block, not just the top of the panel.
    await page.getByTestId('project-security-section').scrollIntoViewIfNeeded();
    await page.screenshot({
      path: `${SCREENSHOTS}/project-docs-meta-saved.png`,
      fullPage: true
    });
  });
});
