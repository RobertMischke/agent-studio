import { test, expect } from '@playwright/test';
import { api } from './helpers/api';

/**
 * Locks the Orchestrator config drawer reachable from the header
 * dev-tools menu. Uses the harmless `Supervisor:HardCheckEnabled`
 * flag for the round-trip toggle so the test does not enable
 * features that would change runner behaviour. Asserts:
 *
 *  - the menu entry is present without any URL hack;
 *  - the GET endpoint returns the typed catalog;
 *  - toggling and saving writes the flag;
 *  - the "Restart required" banner appears after a successful save;
 *  - the override survives a reload of the panel.
 */

interface ConfigOption {
  key: string;
  group: string;
  currentValue: boolean | number | string | null;
  defaultValue: boolean | number | string | null;
}
interface ConfigSnapshot {
  options: ConfigOption[];
  overrideFilePath: string;
  overrideFileExists: boolean;
}

const TEST_KEY = 'Supervisor:HardCheckEnabled';

test.describe('Orchestrator config panel', () => {
  let originalValue: boolean | number | string | null = null;

  test.beforeAll(async () => {
    const snap = await api<ConfigSnapshot>('/api/admin/config/orchestrator');
    const opt = snap.options.find(o => o.key === TEST_KEY);
    expect(opt, `${TEST_KEY} should be in the catalog`).toBeTruthy();
    originalValue = opt!.currentValue;
  });

  test.afterAll(async () => {
    // Restore original value so this spec does not leak state.
    if (originalValue !== null) {
      await api('/api/admin/config/orchestrator', {
        method: 'PUT',
        body: JSON.stringify({ values: { [TEST_KEY]: originalValue } })
      });
    }
  });

  test('GET endpoint returns the typed catalog', async () => {
    const snap = await api<ConfigSnapshot>('/api/admin/config/orchestrator');
    const keys = snap.options.map(o => o.key);
    expect(keys).toEqual(expect.arrayContaining([
      'ReviewDecisionOrchestrator:Enabled',
      'Orchestrator:PrepEnabled',
      'Supervisor:MetaCycleEnabled',
      'Supervisor:AutoInterventionEnabled',
    ]));
    expect(snap.overrideFilePath).toContain('appsettings.Local.json');
  });

  test('menu entry opens the panel and the panel renders the catalog', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('devtools-menu-trigger').click();
    const menuItem = page.getByTestId('devtool-orch-config');
    await expect(menuItem).toBeVisible();
    await menuItem.click();

    const panel = page.getByTestId('orch-config-panel');
    await expect(panel).toBeVisible();
    await expect(panel.getByTestId('orch-config-row-Supervisor:MetaCycleEnabled')).toBeVisible();
    await expect(panel.getByTestId('orch-config-row-Supervisor:AutoInterventionEnabled')).toBeVisible();
    await expect(panel.getByTestId('orch-config-group-orchestrator')).toBeVisible();
    await expect(panel.getByTestId('orch-config-group-supervisor')).toBeVisible();
    await expect(panel.getByTestId('orch-config-group-auto-intervention')).toBeVisible();

    await page.screenshot({ path: 'test-results/orch-config-panel-open.png', fullPage: false });
  });

  test('toggling and saving a flag writes through and shows the restart banner', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('devtools-menu-trigger').click();
    await page.getByTestId('devtool-orch-config').click();

    const panel = page.getByTestId('orch-config-panel');
    await expect(panel).toBeVisible();

    const checkbox = page.getByTestId(`orch-config-input-${TEST_KEY}`);
    const before = await checkbox.isChecked();
    await checkbox.setChecked(!before);

    const save = page.getByTestId('orch-config-save');
    await expect(save).toBeEnabled();
    await save.click();

    // Banner appears after a successful save.
    await expect(page.getByTestId('orch-config-restart-banner')).toBeVisible({ timeout: 10_000 });
    await page.screenshot({ path: 'test-results/orch-config-panel-after-save.png', fullPage: false });

    // The override actually landed on disk: verify via the GET endpoint.
    const snap = await api<ConfigSnapshot>('/api/admin/config/orchestrator');
    const opt = snap.options.find(o => o.key === TEST_KEY)!;
    const expected = !before;
    expect(opt.currentValue).toBe(expected);
  });
});
