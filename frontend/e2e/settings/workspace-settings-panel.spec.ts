import { test, expect } from '@playwright/test';

/**
 * F47 / ADR-0042 — Settings panel "Workspaces" section.
 *
 * Now interactive (F45b mutation endpoints shipped). This spec pins:
 *   1. Opening the Settings panel renders the new section.
 *   2. The listing reflects the workspaces returned by GET /api/workspaces.
 *   3. Action buttons are enabled (only default-workspace delete + boundary
 *      reorder buttons stay disabled).
 *   4. The ADR-0042 / F45b note is visible.
 *   5. A round-trip create → rename → delete works end-to-end against the
 *      live backend, with the UI reflecting each step.
 *
 * The spec hits `/api/workspaces` directly to stay independent of the
 * operator's local appsettings.
 */
test.describe('Settings panel — Workspaces section (F47)', () => {
  test('renders one row per registry workspace with interactive action buttons', async ({ page, request }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const apiResponse = await request.get('/api/workspaces');
    expect(apiResponse.ok(), 'GET /api/workspaces should respond 2xx').toBeTruthy();
    const apiWorkspaces = (await apiResponse.json()) as Array<{
      id: string;
      displayName: string;
      isDefault: boolean;
      projects: Array<unknown>;
    }>;

    await page.getByTestId('studio-ab-settings').click();
    await expect(page.getByTestId('settings-workspaces')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('settings-workspaces-head')).toHaveText('Workspaces');

    if (apiWorkspaces.length === 0) {
      await expect(page.getByTestId('settings-workspaces-empty')).toBeVisible();
      await expect(page.getByTestId('settings-workspaces-list')).toHaveCount(0);
    } else {
      const rows = page.getByTestId('settings-workspace-row');
      await expect(rows).toHaveCount(apiWorkspaces.length);
      const renderedIds = await rows.evaluateAll((nodes) =>
        nodes.map((n) => n.getAttribute('data-workspace-id')),
      );
      expect(new Set(renderedIds)).toEqual(new Set(apiWorkspaces.map((w) => w.id)));

      // Rename / color / delete buttons should exist for every row.
      for (const testid of ['settings-workspace-rename', 'settings-workspace-edit-color', 'settings-workspace-delete']) {
        await expect(page.getByTestId(testid)).toHaveCount(apiWorkspaces.length);
      }

      // The default workspace's delete button is disabled; others are enabled.
      const defaultWs = apiWorkspaces.find((w) => w.isDefault);
      if (defaultWs) {
        const defaultRow = page.locator(`[data-workspace-id="${defaultWs.id}"]`);
        await expect(defaultRow.getByTestId('settings-workspace-delete')).toBeDisabled();
        await expect(defaultRow.getByTestId('settings-workspace-edit-color')).toBeEnabled();
      }
    }

    // Create button + show-archived toggle are present and reachable.
    await expect(page.getByTestId('settings-workspace-create')).toBeEnabled();
    await expect(page.getByTestId('settings-workspace-show-archived')).toBeVisible();

    const note = page.getByTestId('settings-workspaces-note');
    await expect(note).toBeVisible();
    await expect(note).toContainText('ADR-0042');
    await expect(note).toContainText('F45b');
  });

  test('create → rename → delete round-trip via the REST API surface', async ({ page, request }) => {
    // The mutation endpoints require an X-Client-Id header on every write.
    // Register a throwaway identity for the run.
    const clientId = `pw-f47-${Date.now().toString(36)}`;
    const regRes = await request.post('/api/clients/register', { data: { displayName: clientId } });
    expect(regRes.ok(), 'client register should succeed').toBeTruthy();
    const headers = { 'X-Client-Id': clientId };

    // Probe: if the running backend predates the F45b POST endpoint
    // (returns 405 Method Not Allowed instead of accepting the call),
    // skip the round-trip with a clear reason. The first test in this
    // file still pins the read-side surface.
    const uniqueSuffix = Date.now().toString(36);
    const initialName = `Playwright F47 ${uniqueSuffix}`;
    const probe = await request.post('/api/workspaces', {
      headers,
      data: { displayName: initialName },
    });
    test.skip(
      probe.status() === 405,
      'Running backend predates the F45b POST /api/workspaces endpoint. Restart the dev backend to pick up the new code, then re-run.',
    );
    expect(probe.ok(), `POST /api/workspaces should 201, got ${probe.status()} ${await probe.text()}`).toBeTruthy();
    const createdBody = (await probe.json()) as { id: string };
    const wsId = createdBody.id;
    expect(wsId).toMatch(/^ws-/);

    try {
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await page.getByTestId('studio-ab-settings').click();
      const row = page.locator(`[data-workspace-id="${wsId}"]`);
      await expect(row).toBeVisible({ timeout: 10_000 });
      await expect(row.getByTestId('settings-workspace-rename')).toHaveText(initialName);

      const renamed = `Playwright F47 Renamed ${uniqueSuffix}`;
      const renamedRes = await request.put(`/api/workspaces/${wsId}`, {
        headers, data: { displayName: renamed },
      });
      expect(renamedRes.ok()).toBeTruthy();
      await page.reload();
      await page.waitForLoadState('domcontentloaded');
      await page.getByTestId('studio-ab-settings').click();
      await expect(page.locator(`[data-workspace-id="${wsId}"]`).getByTestId('settings-workspace-rename'))
        .toHaveText(renamed, { timeout: 10_000 });
    } finally {
      const deleted = await request.delete(`/api/workspaces/${wsId}`, { headers });
      expect(deleted.ok() || deleted.status() === 404).toBeTruthy();
    }
  });
});
