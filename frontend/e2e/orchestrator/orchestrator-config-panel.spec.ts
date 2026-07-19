import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

/**
 * Locks the orchestrator + supervisor flag UI reachable from the studio
 * Admin/System entry. AGT-1812 retired the standalone Orchestrator Settings modal;
 * the flags now render as the platform-global "Orchestrator" section of the one
 * consolidated Settings view (Global group), backed by the same
 * OrchestratorLogicPanel. The System entry opens that view on the orchestrator
 * section (panel testid `orchestrator-config-overlay`). Uses the
 * harmless `Supervisor:HardCheckEnabled` flag for the round-trip toggle so the
 * test never enables features that would change runner behaviour. Asserts:
 *
 *  - the GET endpoint returns the complete ten-setting catalog retired from
 *    the modal;
 *  - the studio Admin/System entry opens the consolidated Settings view on the
 *    orchestrator section, rendering the logic panel;
 *  - the panel renders rows for all ten settings;
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
const SHOTS = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : 'test-results';
mkdirSync(SHOTS, { recursive: true });

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

const RETIRED_MODAL_SETTING_KEYS = [
  'ReviewDecisionOrchestrator:Enabled',
  'ReviewDecisionOrchestrator:IntervalSeconds',
  'Orchestrator:PrepEnabled',
  'Supervisor:MetaCycleEnabled',
  'Supervisor:SoftReasoningEnabled',
  'Supervisor:HardCheckEnabled',
  'Supervisor:ChatNoteEnabled',
  'Supervisor:AutoInterventionEnabled',
  'Supervisor:AutoInterventionRateLimit',
  'Supervisor:AutoInterventionSeverityThreshold',
];

async function openOrchestratorSettings(page: import('@playwright/test').Page): Promise<void> {
  await page.getByTestId('studio-ab-admin').click();
  await page.getByTestId('studio-admin-open-system').click();
}

test.describe('Orchestrator logic config (consolidated Settings, Admin/System entry)', () => {
  test('GET endpoint returns all ten retired-modal settings', async ({ devBackend }) => {
    const snap = await api<ConfigSnapshot>(devBackend.baseUrl, '/api/admin/config/orchestrator');
    const keys = snap.options.map(o => o.key);
    expect(keys).toEqual(expect.arrayContaining(RETIRED_MODAL_SETTING_KEYS));
    expect(snap.overrideFilePath).toContain('appsettings.Local.json');
  });

  test('Admin/System opens the consolidated Settings view and renders all ten settings', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await openOrchestratorSettings(page);

    // AGT-1812: lands on the consolidated Settings view (Global → Orchestrator),
    // not the retired standalone modal.
    await expect(page.getByTestId('workspace-settings-inline')).toBeVisible();
    const panelHost = page.getByTestId('orchestrator-config-overlay');
    await expect(panelHost).toBeVisible();

    const panel = page.getByTestId('orchestrator-logic-panel');
    await expect(panel).toBeVisible();

    for (const key of RETIRED_MODAL_SETTING_KEYS) {
      await expect(panel.getByTestId(`orchestrator-logic-row-${key}`)).toBeVisible();
    }

    await expect(panel.getByTestId('orchestrator-logic-group-orchestrator')).toBeVisible();
    await expect(panel.getByTestId('orchestrator-logic-group-supervisor')).toBeVisible();
    await expect(panel.getByTestId('orchestrator-logic-group-auto-intervention')).toBeVisible();

    await page.screenshot({ path: path.join(SHOTS, 'orchestrator-global-section--real.png'), fullPage: false });
  });

  test('side-sheet gear opens the same Settings section and the retired modal stays absent', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('orch-side-sheet-toggle').click();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await page.getByTestId('orch-context-badge').click();
    await expect(page.getByTestId('orch-context-menu')).toBeVisible();
    await page.getByTestId('orch-side-sheet-settings').click();

    await expect(page.getByTestId('workspace-settings-inline')).toBeVisible();
    await expect(page.getByTestId('orchestrator-config-overlay')).toBeVisible();
    await expect(page.getByTestId('orchestrator-logic-panel')).toBeVisible();
    await expect(page.getByTestId('orchestrator-settings-modal')).toHaveCount(0);
  });

  test('toggling and saving a flag writes through and shows the saved banner', async ({ page, devBackend }) => {
    const before = await api<ConfigSnapshot>(devBackend.baseUrl, '/api/admin/config/orchestrator');
    const original = before.options.find(o => o.key === TEST_KEY)!.currentValue;

    try {
      await page.goto('/');
      await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

      await openOrchestratorSettings(page);

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
      await page.screenshot({ path: path.join(SHOTS, 'orchestrator-global-section-after-save--real.png'), fullPage: false });

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
