import { test, expect } from '@playwright/test';
import { api } from './helpers/api';

interface WatchPath { path: string; name: string }

const PROJECT_NAME = 'Agent Software Studio';

/**
 * Project-level Security + Architecture sections (prototype).
 *
 * Asserts that the project-detail panel mounts both new sections, the
 * security pane shows the meta header (last review + rating pill +
 * summary) and the file list, and the architecture pane lists ADRs
 * with status pills. Captures one screenshot per section.
 */
test.describe('Project docs sections', () => {
  test('security + architecture sections render in project-detail', async ({ page }) => {
    const projects = await api<WatchPath[]>('/api/watch-paths');
    const target = projects.find(p => p.name === PROJECT_NAME);
    test.skip(!target, `Watch path "${PROJECT_NAME}" not configured on this machine`);

    await page.goto('/');
    const trigger = page.getByTestId(`project-shell-open-${PROJECT_NAME}`);
    await expect(trigger).toBeVisible({ timeout: 10_000 });
    await trigger.click();

    const panel = page.getByTestId('project-detail');
    await expect(panel).toBeVisible();

    // ---- Security ----
    const sec = page.getByTestId('project-security-section');
    await expect(sec).toBeVisible();

    // Seed meta so the header pill + summary render deterministically.
    await api(`/api/projects/${encodeURIComponent(PROJECT_NAME)}/security/meta`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        lastReviewDate: '2026-04-15',
        rating: 'Baseline OK',
        summary: 'Local-only desktop app; threat surface is small.'
      })
    });

    // Force a refresh by re-opening the panel.
    await page.reload();
    await page.getByTestId(`project-shell-open-${PROJECT_NAME}`).click();
    await expect(page.getByTestId('project-security-section')).toBeVisible();

    const meta = page.getByTestId('project-security-meta');
    await expect(meta).toContainText('2026-04-15');
    const rating = page.getByTestId('project-security-rating');
    await expect(rating).toContainText('Baseline OK');

    // The repo seed ships overview.md, requirements.md, and reviews/2026-04-15-baseline.md.
    const files = page.getByTestId('project-security-files');
    await expect(files).toBeVisible();
    await expect(files).toContainText('overview.md');
    await expect(files).toContainText('requirements.md');

    // Open one file and confirm the rendered viewer fills with content.
    await page.getByTestId('project-security-file-overview.md').click();
    const viewer = page.getByTestId('project-security-viewer');
    await expect(viewer).toBeVisible();
    await expect(viewer).toContainText('Security overview');

    await sec.scrollIntoViewIfNeeded();
    await sec.screenshot({ path: 'test-results/project-docs-security.png' });

    // ---- Architecture ----
    const arch = page.getByTestId('project-architecture-section');
    await arch.scrollIntoViewIfNeeded();
    await expect(arch).toBeVisible();

    const list = page.getByTestId('project-architecture-list');
    await expect(list).toBeVisible();
    await expect(list).toContainText('ADR-0001');

    // Toggle the first ADR open and confirm a body appears with status badge.
    await page.getByTestId('project-architecture-ADR-0001').click();
    await expect(page.getByTestId('project-architecture-viewer')).toBeVisible();

    await arch.screenshot({ path: 'test-results/project-docs-architecture.png' });
  });
});
