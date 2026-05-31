import { test, expect } from '../fixtures/dev-backend';

/**
 * Locks the orchestrator + supervisor flag UI reachable from the header
 * dev-tools menu. Today that surface is the "Orchestrator" section of the
 * Orchestrator Settings modal, backed by OrchestratorLogicPanel; the menu
 * item `orch-config` opens the modal and the modal renders the logic panel
 * on its default (orchestrator) rail. Uses the harmless
 * `Supervisor:HardCheckEnabled` flag for the round-trip toggle so the test
 * never enables features that would change runner behaviour. Asserts:
 *
 *  - the GET endpoint returns the typed catalog with the four toggles the
 *    task explicitly calls out (review-decision, prep, soft-reasoning,
 *    meta-cycle);
 *  - the dev-tools menu entry opens the settings modal on the logic panel;
 *  - the panel renders rows for all four targeted toggles;
 *  - toggling and saving writes the flag to disk (gated by X-Client-Id);
 *  - the "saved" banner appears after a successful save.
 *
 * Uses the `dev-backend` fixture so the spec is runnable from stable's
 * Playwright suite. Per AGENTS.md ("Dev backend lifecycle: Playwright-only")
 * that fixture is the only sanctioned path that brings dev's backend up; the
 * earlier run authored these checks but could not execute them against a
 * running backend because the spec did not pull in the fixture. The fixture
 * is idempotent: if dev is already up it is left running on teardown.
 */

const CLIENT_ID = 'local-default';

async function api<T>(baseUrl: string, path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(`${baseUrl}${path}`, {
    headers: {
      'content-type': 'application/json',
      'x-client-id': CLIENT_ID,
      ...(init.headers ?? {})
    },
    ...init
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`API ${init.method ?? 'GET'} ${path} -> ${res.status} ${res.statusText}\n${text}`);
  }
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

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

test.describe('Orchestrator logic config (settings modal, dev-tools menu)', () => {
  test('GET endpoint returns the typed catalog with the four targeted toggles', async ({ devBackend }) => {
    const snap = await api<ConfigSnapshot>(devBackend.baseUrl, '/api/admin/config/orchestrator');
    const keys = snap.options.map(o => o.key);
    expect(keys).toEqual(expect.arrayContaining(TARGETED_TOGGLES));
    expect(snap.overrideFilePath).toContain('appsettings.Local.json');
  });

  test('dev-tools menu opens the settings modal and renders the four targeted toggles', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('devtools-menu-trigger').click();
    const menuItem = page.getByTestId('devtools-menu-item-orch-config');
    await expect(menuItem).toBeVisible();
    await menuItem.click();

    await expect(page.getByTestId('orchestrator-settings-modal')).toBeVisible();

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

  test('toggling and saving a flag writes through and shows the saved banner', async ({ page, devBackend }) => {
    const before = await api<ConfigSnapshot>(devBackend.baseUrl, '/api/admin/config/orchestrator');
    const original = before.options.find(o => o.key === TEST_KEY)!.currentValue;

    try {
      await page.goto('/');
      await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

      await page.getByTestId('devtools-menu-trigger').click();
      await page.getByTestId('devtools-menu-item-orch-config').click();

      const panel = page.getByTestId('orchestrator-logic-panel');
      await expect(panel).toBeVisible();

      const row = panel.getByTestId(`orchestrator-logic-row-${TEST_KEY}`);
      const checkbox = row.locator('input[type="checkbox"]');
      const checkedBefore = await checkbox.isChecked();
      await checkbox.setChecked(!checkedBefore);

      const save = page.getByTestId('orchestrator-logic-save');
      await expect(save).toBeEnabled();
      await save.click();

      await expect(page.getByTestId('orchestrator-logic-applied')).toBeVisible({ timeout: 10_000 });
      await page.screenshot({ path: 'test-results/orch-logic-panel-after-save.png', fullPage: false });

      const after = await api<ConfigSnapshot>(devBackend.baseUrl, '/api/admin/config/orchestrator');
      expect(after.options.find(o => o.key === TEST_KEY)!.currentValue).toBe(!checkedBefore);
    } finally {
      if (original !== null) {
        await api(devBackend.baseUrl, '/api/admin/config/orchestrator', {
          method: 'PUT',
          body: JSON.stringify({ values: { [TEST_KEY]: original } })
        });
      }
    }
  });
});
