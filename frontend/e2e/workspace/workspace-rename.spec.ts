import { test, expect, Page } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * F46 Step 2: inline-rename of a workspace folder header in the Explorer tree.
 * Double-clicking a real workspace header swaps it for a text input; Enter
 * commits (PUT /api/workspaces/{id}) and Escape cancels. The rename is a
 * registry-metadata mutation only — no project folder is moved or renamed on
 * disk, so the project rows under the header are unchanged afterwards.
 *
 * Runs against the live dev stack. Each test renames a real workspace and
 * then restores its original name (via the API) so the operator's registry is
 * left exactly as found. When the registry exposes no real workspace folder
 * (empty / in-memory mode) the contract is skipped.
 */

const dest = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.join(__dirname, '..', '..', 'test-results', 'f46-screenshots');

const ROW = '[data-testid^="studio-explorer-project-row-"]';

interface WsLite { id: string; displayName: string }

async function gotoStudio(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 10_000 });
}

/** First registry workspace that is a real folder (synthetic groups use a
 *  "__"-prefixed id and are not renamable). */
async function firstRealWorkspace(page: Page): Promise<WsLite | null> {
  const all = await page.evaluate(async () => {
    const res = await fetch('/api/workspaces');
    if (!res.ok) return [] as WsLite[];
    return (await res.json()) as WsLite[];
  });
  return all.find(w => !w.id.startsWith('__')) ?? null;
}

async function workspaceName(page: Page, id: string): Promise<string | undefined> {
  return page.evaluate(async (wsId) => {
    const res = await fetch('/api/workspaces');
    if (!res.ok) return undefined;
    const list = (await res.json()) as WsLite[];
    return list.find(w => w.id === wsId)?.displayName;
  }, id);
}

async function restoreName(page: Page, id: string, displayName: string): Promise<void> {
  // The backend's registration boundary requires the X-Client-Id header that
  // the app's HttpClient interceptor stamps; a bare fetch is rejected (401),
  // which would leave the registry mutated. Sign the restore as the bootstrap
  // identity so cleanup actually lands.
  await page.evaluate(async ({ wsId, name }) => {
    await fetch(`/api/workspaces/${wsId}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json', 'X-Client-Id': 'local-default' },
      body: JSON.stringify({ displayName: name }),
    });
  }, { wsId: id, name: displayName });
}

test.describe('F46: workspace-header inline rename', () => {
  test.beforeAll(() => {
    fs.mkdirSync(dest, { recursive: true });
  });

  test('double-click opens an inline input; Enter commits the rename', async ({ page }) => {
    await gotoStudio(page);
    const ws = await firstRealWorkspace(page);
    if (!ws) {
      test.skip(true, 'No real registry workspace — rename contract skipped.');
      return;
    }

    const header = page.getByTestId(`studio-explorer-ws-group-${ws.id}`);
    await expect(header).toBeVisible({ timeout: 10_000 });

    // Project rows owned by this workspace, captured to prove the rename does
    // not add/remove/move any project (registry-only mutation).
    const rowsBefore = await page.locator(ROW).count();

    await header.dblclick();
    const input = page.getByTestId(`studio-explorer-ws-rename-input-${ws.id}`);
    await expect(input).toBeVisible();
    await expect(input).toBeFocused();
    await input.screenshot({ path: path.join(dest, 'f46-rename-inline-input.png') });

    const renamed = `${ws.displayName} (renamed)`;
    try {
      await input.fill(renamed);
      await input.press('Enter');

      // Input closes back to the header, and the registry reflects the change.
      await expect(input).toHaveCount(0);
      await expect.poll(() => workspaceName(page, ws.id)).toBe(renamed);

      // No project folders were moved/renamed: row count is unchanged.
      await expect.poll(() => page.locator(ROW).count()).toBe(rowsBefore);
    } finally {
      await restoreName(page, ws.id, ws.displayName);
    }

    expect(await workspaceName(page, ws.id)).toBe(ws.displayName);
  });

  test('right-click opens a text-only Rename menu that starts the inline rename', async ({ page }) => {
    await gotoStudio(page);
    const ws = await firstRealWorkspace(page);
    if (!ws) {
      test.skip(true, 'No real registry workspace — rename contract skipped.');
      return;
    }

    const header = page.getByTestId(`studio-explorer-ws-group-${ws.id}`);
    await expect(header).toBeVisible({ timeout: 10_000 });

    await header.click({ button: 'right' });

    const panel = page.getByTestId('studio-explorer-ws-ctx-panel');
    await expect(panel).toBeVisible({ timeout: 3_000 });
    await expect(panel.getByTestId('studio-explorer-ws-ctx-item-rename')).toHaveText('Rename');

    // Menu convention: text-only, no decorative icons.
    await expect(panel.locator('.app-menu__icon')).toHaveCount(0);
    await expect(panel.locator('img')).toHaveCount(0);
    await expect(panel.locator('svg')).toHaveCount(0);

    await panel.screenshot({ path: path.join(dest, 'f46-rename-context-menu.png') });

    await panel.getByTestId('studio-explorer-ws-ctx-item-rename').click();

    // The context-menu route opens the same inline rename input as double-click.
    const input = page.getByTestId(`studio-explorer-ws-rename-input-${ws.id}`);
    await expect(input).toBeVisible();
    await expect(input).toBeFocused();

    // Cancel without persisting so the operator's registry is left untouched.
    await input.press('Escape');
    await expect(input).toHaveCount(0);
    expect(await workspaceName(page, ws.id)).toBe(ws.displayName);
  });

  test('Escape cancels the rename and leaves the name unchanged', async ({ page }) => {
    await gotoStudio(page);
    const ws = await firstRealWorkspace(page);
    if (!ws) {
      test.skip(true, 'No real registry workspace — rename contract skipped.');
      return;
    }

    const header = page.getByTestId(`studio-explorer-ws-group-${ws.id}`);
    await expect(header).toBeVisible({ timeout: 10_000 });

    await header.dblclick();
    const input = page.getByTestId(`studio-explorer-ws-rename-input-${ws.id}`);
    await expect(input).toBeVisible();

    await input.fill(`${ws.displayName} DO NOT SAVE`);
    await input.press('Escape');

    await expect(input).toHaveCount(0);
    await expect(header).toBeVisible();
    expect(await workspaceName(page, ws.id)).toBe(ws.displayName);
  });
});
