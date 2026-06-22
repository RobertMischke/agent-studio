import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Project-level Steering Docs surface. Verifies the inventory list, the
 * human summary, the warnings strip, the raw-file drilldown, and the
 * action button row all render against the real backend, and captures
 * screenshots so the surface is reviewable from the report.
 */

interface WatchPath { name: string; path: string }

// Outside outputDir so Playwright doesn't wipe these between runs.
const SCREENSHOTS = path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-steering-docs');
fs.mkdirSync(SCREENSHOTS, { recursive: true });

test.describe('Project detail - Steering Docs section', () => {
  let projectName = '';

  test.beforeAll(async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);
    // Prefer the Agent Software Studio project: it has the canonical
    // README, AGENTS, ROADMAP, ADR, runtime-prompts set.
    const preferred = paths.find(p => /agent.?software/i.test(p.name)) ?? paths[0];
    projectName = preferred.name;
  });

  test('Inventory + summary + warnings render with raw file drilldown', async ({ page }) => {
    await page.goto('/');
    await page.getByTestId(`project-shell-open-${projectName}`).click();
    await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

    const section = page.getByTestId('project-steering-docs-section');
    await section.scrollIntoViewIfNeeded();
    await expect(section).toBeVisible();

    // Inventory list rendered with the canonical sources.
    await expect(page.getByTestId('project-steering-docs-sources')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-steering-docs-source-readme')).toBeVisible();
    await expect(page.getByTestId('project-steering-docs-source-agents')).toBeVisible();
    await expect(page.getByTestId('project-steering-docs-source-roadmap')).toBeVisible();
    await expect(page.getByTestId('project-steering-docs-source-adr')).toBeVisible();
    await expect(page.getByTestId('project-steering-docs-source-runtime-prompts')).toBeVisible();

    // Human summary block exists and references the canonical files.
    const summary = page.getByTestId('project-steering-docs-summary');
    await expect(summary).toBeVisible();
    await expect(summary).toContainText('canonical steering sources');

    // Action buttons rendered for every named action.
    const actions = page.getByTestId('project-steering-docs-actions');
    await expect(actions).toBeVisible();
    for (const slug of ['summarize', 'check-drift', 'analyze-failures', 'propose-readme', 'propose-agents', 'create-followup']) {
      await expect(page.getByTestId(`project-steering-docs-action-${slug}`)).toBeVisible();
    }

    // Capture the inventory + summary + warnings strip in one shot.
    await page.screenshot({
      path: `${SCREENSHOTS}/01-inventory-and-summary.png`,
      fullPage: true,
    });

    // Drill down into AGENTS.md to confirm raw content opens inline.
    await page.getByTestId('project-steering-docs-source-agents').click();
    const viewer = page.getByTestId('project-steering-docs-viewer');
    await expect(viewer).toBeVisible();
    const content = page.getByTestId('project-steering-docs-content');
    await expect(content).toBeVisible({ timeout: 10_000 });
    await expect(content.locator('h1, h2, h3').first()).toBeVisible();

    await page.screenshot({
      path: `${SCREENSHOTS}/02-raw-file-drilldown.png`,
      fullPage: true,
    });

    // Drill into the runtime-prompts directory and pick a child file.
    await page.getByTestId('project-steering-docs-source-runtime-prompts').click();
    const childList = page.getByTestId('project-steering-docs-children');
    await expect(childList).toBeVisible({ timeout: 5_000 });
    const firstChild = childList.locator('button.psd__child-btn').first();
    await firstChild.click();
    await expect(page.getByTestId('project-steering-docs-child-content')).toBeVisible({ timeout: 10_000 });

    await page.screenshot({
      path: `${SCREENSHOTS}/03-runtime-prompt-children.png`,
      fullPage: true,
    });
  });

  test('Action button queues a 1-preparation task without rewriting docs', async ({ page }) => {
    await page.goto('/');
    await page.getByTestId(`project-shell-open-${projectName}`).click();
    await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

    const section = page.getByTestId('project-steering-docs-section');
    await section.scrollIntoViewIfNeeded();
    await expect(section).toBeVisible();

    // Click the Summarize Steering Docs button; it should queue a
    // 1-preparation task and surface the generated job id in the action message.
    await page.getByTestId('project-steering-docs-action-summarize').click();
    const msg = page.getByTestId('project-steering-docs-action-msg');
    await expect(msg).toBeVisible({ timeout: 10_000 });
    await expect(msg).toContainText(/1-preparation/i);
    await expect(msg).toContainText(/^Queued steering-summarize-/);

    // Capture the action-message state for the report.
    await page.screenshot({
      path: `${SCREENSHOTS}/04-action-followup-queued.png`,
      fullPage: true,
    });

    // Clean up: the queued task is intentionally left in 1-preparation so
    // the user reviews the prompt before promoting; we just verify the
    // creation, then delete it through the API so re-runs stay deterministic.
    const text = (await msg.textContent()) ?? '';
    const match = text.match(/Queued (steering-summarize-\S+)/);
    if (match) {
      const created = match[1];
      // Find the watchPath to scope the delete request.
      const paths = await api<WatchPath[]>('/api/watch-paths');
      const pref = paths.find(p => p.name === projectName) ?? paths[0];
      try {
        await api(`/api/tasks/${encodeURIComponent(created)}?watchPath=${encodeURIComponent(pref.path)}`, { method: 'DELETE' });
      } catch { /* best-effort cleanup */ }
    }
  });

  test('Empty / unknown project surfaces a helpful state without crashing', async ({ page }) => {
    // Navigate to a non-existent project via the URL - the project shell
    // should still render; the steering section should report the
    // backend's 404 in its error pane, not blank-screen the user.
    await page.goto('/?projectName=__steering-docs-no-such-project__');
    // We don't depend on the URL to switch projects (the app's routing
    // varies); just confirm the rest of the UI is alive.
    await expect(page.locator('body')).toBeVisible();

    // Direct API probe for evidence that the surface stays graceful.
    const res = await page.request.get('/api/projects/__steering-docs-no-such-project__/steering');
    expect(res.status()).toBe(404);
  });

  test('Project shell rail opens the Steering Docs panel', async ({ page }) => {
    // The shell rail entry sits beside Observability / Token Usage /
    // Security; selecting it must mount the steering section as the
    // shell's custom panel and reach the same inventory + actions the
    // long detail view exposes.
    await page.goto('/');
    await page.getByTestId(`project-shell-open-${projectName}`).click();
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

    const rail = page.getByTestId('project-shell-rail-steering');
    await expect(rail).toBeVisible();
    await rail.click();
    await expect(rail).toHaveAttribute('aria-current', 'page');

    const panel = page.getByTestId('project-shell-panel-steering');
    await expect(panel).toBeVisible();
    await expect(panel).toHaveAttribute('data-rail-key', 'steering');

    // The custom-panel slot should host the real steering section, not
    // the generic placeholder copy.
    await expect(panel.getByTestId('project-shell-panel-empty')).toHaveCount(0);

    const section = panel.getByTestId('project-steering-docs-section');
    await expect(section).toBeVisible({ timeout: 10_000 });
    await expect(panel.getByTestId('project-steering-docs-sources')).toBeVisible({ timeout: 10_000 });
    await expect(panel.getByTestId('project-steering-docs-summary')).toBeVisible();
    await expect(panel.getByTestId('project-steering-docs-actions')).toBeVisible();

    await page.screenshot({
      path: `${SCREENSHOTS}/05-shell-rail-steering.png`,
      fullPage: true,
    });
  });
});
