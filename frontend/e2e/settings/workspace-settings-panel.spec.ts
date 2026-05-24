import { test, expect } from '@playwright/test';

/**
 * F47 / ADR-0037 — Settings panel "Workspaces" section.
 *
 * Read-only registry listing today; the mutation buttons (color edit,
 * move up / down, delete, and the "+ New workspace" affordance) ship
 * with F45b. This spec pins:
 *   1. Opening the Settings panel renders the new section.
 *   2. The listing reflects the workspaces returned by GET /api/workspaces.
 *   3. Every action button is present but disabled, with a tooltip that
 *      points at the F45b follow-up.
 *   4. The "ships with F45b" note is visible.
 *
 * The spec is intentionally light: it does not assert specific workspace
 * ids / names (those depend on the operator's appsettings.Local.json).
 * It only asserts the relationship between the API payload and the DOM.
 */
test.describe('Settings panel — Workspaces section (F47)', () => {
  test('renders one row per registry workspace with disabled action buttons', async ({ page, request }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    // Source of truth: hit the registry endpoint directly so we don't
    // depend on what the operator happens to have in appsettings.
    const apiResponse = await request.get('/api/workspaces');
    expect(apiResponse.ok(), 'GET /api/workspaces should respond 2xx').toBeTruthy();
    const apiWorkspaces = (await apiResponse.json()) as Array<{
      id: string;
      displayName: string;
      isDefault: boolean;
      projects: Array<unknown>;
    }>;

    // Open the Settings panel via the activity-bar gear button.
    await page.getByTestId('studio-ab-settings').click();
    await expect(page.getByTestId('settings-workspaces')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('settings-workspaces-head')).toHaveText('Workspaces');

    if (apiWorkspaces.length === 0) {
      // Empty-state branch: no list, just the seed-hint.
      await expect(page.getByTestId('settings-workspaces-empty')).toBeVisible();
      await expect(page.getByTestId('settings-workspaces-list')).toHaveCount(0);
      return;
    }

    // Listing branch: one row per workspace; ids must match.
    const rows = page.getByTestId('settings-workspace-row');
    await expect(rows).toHaveCount(apiWorkspaces.length);
    const renderedIds = await rows.evaluateAll((nodes) =>
      nodes.map((n) => n.getAttribute('data-workspace-id')),
    );
    expect(new Set(renderedIds)).toEqual(new Set(apiWorkspaces.map((w) => w.id)));

    // Every action button on every row is disabled until F45b ships.
    for (const testid of [
      'settings-workspace-edit-color',
      'settings-workspace-move-up',
      'settings-workspace-move-down',
      'settings-workspace-delete',
    ]) {
      const buttons = page.getByTestId(testid);
      const count = await buttons.count();
      expect(count, `${testid} should render once per workspace row`).toBe(apiWorkspaces.length);
      for (let i = 0; i < count; i++) {
        await expect(buttons.nth(i)).toBeDisabled();
      }
    }

    // The note that points at ADR-0037 must be present.
    const note = page.getByTestId('settings-workspaces-note');
    await expect(note).toBeVisible();
    await expect(note).toContainText('ADR-0037');
    await expect(note).toContainText('F45b');
  });
});
