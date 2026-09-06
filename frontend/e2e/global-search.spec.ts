import { test, expect } from './fixtures/dev-backend';
import type { Page } from '@playwright/test';

const DOSSIER_PROJECT = 'Agent Studio';
const DOSSIER_ID = 'orchestrator-waechter';
const DOSSIER_KEY = 'AGT-W15';

/**
 * The palette is the surface under test, not the sign-in gate. A dev backend
 * on the networked profile would answer with the login screen; this keeps the
 * spec on the shell regardless of the profile the fixture happened to start.
 */
function stubAuth(page: Page): Promise<void> {
  return page.route('**/api/auth/status', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
}

test('global palette groups results and supports keyboard navigation in both themes', async ({ page }) => {
  await stubAuth(page);
  await page.route('**/api/search?**', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ query: 'README', tasks: [], commits: [], dossiers: [], errors: {}, durationMs: 7, files: [{
      domain: 'files', projectName: 'Agent Studio', projectColor: '#569cd6', title: 'README.md',
      subtitle: 'README.md', path: 'README.md', isWiki: false,
    }] }),
  }));

  await page.addInitScript(() => localStorage.setItem('atp.studio.theme', 'light'));
  await page.goto('/');
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused();
  await input.fill('README');
  await expect(page.getByTestId('global-search-group-files')).toContainText('README.md');
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[role="option"][aria-selected="true"]')).toHaveCount(1);

  // A backendless worktree serve can report runner-status startup noise after
  // the palette has opened. Keep that unrelated global dialog out of the
  // feature evidence frame.
  await page.getByTestId('error-dialog-close').click({ timeout: 2_000 }).catch(() => {});

  const screenshotPath = process.env.GLOBAL_SEARCH_SCREENSHOT;
  if (screenshotPath) await page.screenshot({ path: screenshotPath, fullPage: true });

  await page.keyboard.press('Escape');
  await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
  await page.reload();
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await expect(page.getByTestId('global-search-input')).toBeVisible();
});

test('a dossier key opens the dossier viewer from the palette', async ({ page }) => {
  await stubAuth(page);
  await page.route('**/api/search?**', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      query: DOSSIER_KEY, tasks: [], commits: [], files: [], errors: {}, durationMs: 4,
      dossiers: [{
        domain: 'dossiers', projectName: DOSSIER_PROJECT, projectColor: '#569cd6',
        title: 'Watcher', subtitle: 'active · decision-ready', dossierKey: DOSSIER_KEY,
        dossierId: DOSSIER_ID, summary: 'Autonomous problem finding with ticket proposals.',
      }],
    }),
  }));
  await page.route(`**/api/projects/*/workbenches/${DOSSIER_ID}`, route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      workbench: {
        id: DOSSIER_ID, key: DOSSIER_KEY, title: 'Watcher',
        summary: 'Autonomous problem finding with ticket proposals.',
        status: 'active', phase: 'decision-ready', updatedAtUtc: '2026-09-01T10:00:00Z',
        entryPath: 'docs/operations/orchestrator-waechter/index.html',
        valid: true, error: null, sourceTaskKeys: [], relatedTaskKeys: [], pattern: 'concept',
      },
      html: '<html><body><h1>Watcher</h1></body></html>',
      branch: 'develop', revision: null, workingTreeModified: false, fingerprint: null,
    }),
  }));

  await page.goto('/');
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await page.getByTestId('global-search-input').fill(DOSSIER_KEY);

  const group = page.getByTestId('global-search-group-dossiers');
  await expect(group).toContainText(DOSSIER_KEY);
  await expect(group).toContainText('active · decision-ready');
  await expect(group).toContainText('Autonomous problem finding with ticket proposals.');

  // A backendless worktree serve reports runner-status startup noise over the
  // palette. Keep that unrelated global dialog out of the feature evidence.
  await page.getByTestId('error-dialog-close').click({ timeout: 2_000 }).catch(() => {});

  const screenshotPath = process.env.DOSSIER_SEARCH_SCREENSHOT;
  if (screenshotPath) await page.screenshot({ path: screenshotPath, fullPage: true });

  // Enter selects the highlighted first result, so this also proves the
  // Dossier group joins the one keyboard list the palette navigates.
  await expect(group.getByRole('option').first()).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Enter');

  await expect(page.getByTestId('workbench-viewer')).toBeVisible();
  await expect(page.getByTestId('workbench-viewer-title')).toHaveText('Watcher');
});
