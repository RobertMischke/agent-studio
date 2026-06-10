import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Per-project CLI permission modes (YOLO default).
 *
 * Covers the test-path acceptance criterion: toggle a CLI's mode in the
 * Project Settings UI, then prove the reloadable effective-mode probe
 * (`GET /api/cli/{name}/effective-mode?project=...`) reports the new mode +
 * source + spawned args — i.e. the change takes effect for the next spawn
 * without a backend restart.
 *
 * Runs against the dedicated "Playwright Test" project so the override never
 * disturbs a real project, and restores the CLI it touches in afterAll.
 */

interface WatchPath { name: string; path: string }
interface CliModesResponse {
  resolved: Record<string, { mode: string; source: string; args: string[] }>;
  overrides: Record<string, string>;
  available: string[];
}
interface EffectiveModeResponse {
  cli: string;
  project: string;
  mode: string;
  source: string;
  args: string[];
}

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'cli-permission-modes');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'cli-permission-modes');
})();

// Codex is the CLI we drive: read-only renders a stable, distinctive arg pair
// (--sandbox read-only) so the probe assertion is unambiguous.
const CLI = 'codex';
const TARGET_MODE = 'read-only';

let projectName = '';
let originalOverride = '';

function enc(name: string): string {
  return encodeURIComponent(name);
}

async function getCliModes(): Promise<CliModesResponse> {
  return api<CliModesResponse>(`/api/projects/${enc(projectName)}/cli-modes`);
}

async function setCliMode(cli: string, mode: string): Promise<void> {
  await api(`/api/projects/${enc(projectName)}/cli-mode`, {
    method: 'PUT',
    body: JSON.stringify({ cliType: cli, mode }),
  });
}

async function probe(cli: string): Promise<EffectiveModeResponse> {
  return api<EffectiveModeResponse>(`/api/cli/${cli}/effective-mode?project=${enc(projectName)}`);
}

/**
 * Nav-rebuild step 2 (T5b): per-project CLI permission modes moved out of
 * Project Settings into the workspace Admin → CLI & Modelle surface, where
 * the same control renders scoped to the active project. Seed the studio tab
 * state so `projectName` is the sole active project (the active-tab effect
 * runs setSoleProject), open the Admin panel, and return its CLI-modes block.
 */
async function openAdminCliModes(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.evaluate((name) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__', sticky: true },
        { kind: 'board', projectName: name },
      ],
      activeKey: `board:${name}`,
    }));
    localStorage.setItem('activeProjects', JSON.stringify([name]));
    location.hash = '';
  }, projectName);
  await page.reload();
  await page.waitForLoadState('domcontentloaded');

  await page.getByTestId('studio-ab-admin').click();
  const adminCliModes = page.getByTestId('studio-admin-cli-modes');
  await expect(adminCliModes).toBeVisible({ timeout: 10_000 });
  return adminCliModes;
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;

  originalOverride = (await getCliModes()).overrides[CLI] ?? '';
});

test.afterAll(async () => {
  if (!projectName) return;
  // Restore the CLI we touched to its pre-test override (empty clears it).
  await setCliMode(CLI, originalOverride);
});

test('admin: CLI modes render with YOLO default + warning banner, toggle reaches the probe', async ({ page }) => {
  // Start from a clean slate so the default-source assertion is deterministic
  // regardless of what a prior run left behind.
  await setCliMode(CLI, '');

  await openAdminCliModes(page);

  const section = page.getByTestId('project-detail-cli-modes');
  await expect(section).toBeVisible();

  // The English warning banner states YOLO is the orchestration default.
  const warning = page.getByTestId('project-detail-cli-mode-warning');
  await expect(warning).toBeVisible();
  await expect(warning).toContainText('YOLO is the default for agent-orchestrated operation');

  // Every CLI dropdown is pre-selected to the backend-resolved effective mode.
  const resolved = (await getCliModes()).resolved;
  for (const [cli, r] of Object.entries(resolved)) {
    const select = page.getByTestId(`cli-mode-select-${cli}`);
    if (await select.count() === 0) continue;
    await expect(select).toHaveValue(r.mode);
  }

  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeInViewport();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-settings-defaults.png'), fullPage: true });

  // Toggle Codex to Read-only through the UI and confirm the write round-trips
  // to the backend override map.
  const codexSelect = page.getByTestId(`cli-mode-select-${CLI}`);
  await codexSelect.selectOption(TARGET_MODE);
  await expect(codexSelect).toHaveValue(TARGET_MODE);

  await expect.poll(async () => (await getCliModes()).overrides[CLI]).toBe(TARGET_MODE);

  // The reloadable probe a driver would consult on spawn now reports the new
  // mode, sourced to the project, with the matching sandbox args.
  await expect.poll(async () => (await probe(CLI)).mode).toBe(TARGET_MODE);
  const eff = await probe(CLI);
  expect(eff.source).toBe('project');
  expect(eff.args).toEqual(['--sandbox', 'read-only']);

  // The source chip flips to "project" and the args preview reflects the flags.
  await expect(page.getByTestId(`cli-mode-source-${CLI}`)).toHaveText('project');
  await expect(page.getByTestId(`cli-mode-args-${CLI}`)).toContainText('--sandbox read-only');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-settings-codex-readonly.png'), fullPage: true });

  // Reset reverts the override; the probe falls back to global/default.
  await page.getByTestId(`cli-mode-reset-${CLI}`).click();
  await expect.poll(async () => (await getCliModes()).overrides[CLI]).toBeUndefined();
  await expect(page.getByTestId(`cli-mode-source-${CLI}`)).not.toHaveText('project');
});
