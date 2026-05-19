import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Locks the orchestrator + supervisor flag UI reachable from the
 * header dev-tools menu. Today that surface is the "Logic" tab of
 * the orchestrator side sheet, backed by OrchestratorLogicPanel.
 * Uses the harmless `Supervisor:HardCheckEnabled` flag for the
 * round-trip toggle so the test does not enable features that
 * would change runner behaviour. Asserts:
 *
 *  - the GET endpoint returns the typed catalog with the four
 *    toggles the task explicitly calls out (review-decision, prep,
 *    soft-reasoning, meta-cycle);
 *  - the dev-tools menu entry opens the side sheet on the Logic tab;
 *  - the panel renders rows for all four targeted toggles;
 *  - toggling and saving writes the flag to disk;
 *  - the "saved" banner appears after a successful save.
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

const TARGETED_TOGGLES = [
  'ReviewDecisionOrchestrator:Enabled',
  'Orchestrator:PrepEnabled',
  'Supervisor:SoftReasoningEnabled',
  'Supervisor:MetaCycleEnabled',
];

test.describe('Orchestrator logic config (side-sheet Logic tab)', () => {
  let originalValue: boolean | number | string | null = null;

  test.beforeAll(async () => {
    const snap = await api<ConfigSnapshot>('/api/admin/config/orchestrator');
    const opt = snap.options.find(o => o.key === TEST_KEY);
    expect(opt, `${TEST_KEY} should be in the catalog`).toBeTruthy();
    originalValue = opt!.currentValue;
  });

  test.afterAll(async () => {
    if (originalValue !== null) {
      await api('/api/admin/config/orchestrator', {
        method: 'PUT',
        body: JSON.stringify({ values: { [TEST_KEY]: originalValue } })
      });
    }
  });

  test('GET endpoint returns the typed catalog with the four targeted toggles', async () => {
    const snap = await api<ConfigSnapshot>('/api/admin/config/orchestrator');
    const keys = snap.options.map(o => o.key);
    expect(keys).toEqual(expect.arrayContaining(TARGETED_TOGGLES));
    expect(snap.overrideFilePath).toContain('appsettings.Local.json');
  });

  test('dev-tools menu opens the side-sheet Logic tab and renders the four targeted toggles', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('devtools-menu-trigger').click();
    const menuItem = page.getByTestId('devtool-orch-config');
    await expect(menuItem).toBeVisible();
    await menuItem.click();

    const panel = page.getByTestId('orchestrator-logic-panel');
    await expect(panel).toBeVisible();

    for (const key of TARGETED_TOGGLES) {
      await expect(panel.getByTestId(`orchestrator-logic-row-${key}`)).toBeVisible();
    }

    await expect(panel.getByTestId('orchestrator-logic-group-orchestrator')).toBeVisible();
    await expect(panel.getByTestId('orchestrator-logic-group-supervisor')).toBeVisible();
    await expect(panel.getByTestId('orchestrator-logic-group-auto-intervention')).toBeVisible();

    await page.screenshot({ path: 'test-results/orch-logic-panel-open.png', fullPage: false });
  });

  test('toggling and saving a flag writes through and shows the saved banner', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('devtools-menu-trigger').click();
    await page.getByTestId('devtool-orch-config').click();

    const panel = page.getByTestId('orchestrator-logic-panel');
    await expect(panel).toBeVisible();

    const row = panel.getByTestId(`orchestrator-logic-row-${TEST_KEY}`);
    const checkbox = row.locator('input[type="checkbox"]');
    const before = await checkbox.isChecked();
    await checkbox.setChecked(!before);

    const save = page.getByTestId('orchestrator-logic-save');
    await expect(save).toBeEnabled();
    await save.click();

    await expect(page.getByTestId('orchestrator-logic-applied')).toBeVisible({ timeout: 10_000 });
    await page.screenshot({ path: 'test-results/orch-logic-panel-after-save.png', fullPage: false });

    const snap = await api<ConfigSnapshot>('/api/admin/config/orchestrator');
    const opt = snap.options.find(o => o.key === TEST_KEY)!;
    const expected = !before;
    expect(opt.currentValue).toBe(expected);
  });
});
