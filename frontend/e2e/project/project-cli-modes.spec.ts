import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Project CLI permission-mode regression path.
 *
 * This is the ticket's "UI toggle -> reloadable effective-mode probe" check:
 * set a known non-YOLO Codex override, switch it to YOLO through Project
 * Settings, then verify the backend probe reports the exact mode and flags the
 * next spawn would use. The original project override is restored in finally so
 * the spec stays idempotent.
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

let projectName = '';

async function setCliMode(project: string, cliType: string, mode: string): Promise<void> {
  await api(`/api/projects/${encodeURIComponent(project)}/cli-mode`, {
    method: 'PUT',
    body: JSON.stringify({ cliType, mode }),
  });
}

async function effectiveMode(project: string, cliType: string): Promise<EffectiveModeResponse> {
  return api<EffectiveModeResponse>(
    `/api/cli/${encodeURIComponent(cliType)}/effective-mode?project=${encodeURIComponent(project)}`,
  );
}

/**
 * Nav-rebuild step 2 (T5b): per-project CLI permission modes moved out of
 * Project Settings into the workspace Admin → CLI & Modelle surface, scoped
 * to the active project. Seed the studio tab state so `projectName` is the
 * sole active project (the active-tab effect runs setSoleProject), open the
 * Admin panel, and return its CLI-modes block.
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
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThanOrEqual(1);
  const preferred = paths.find(p => /agent.?task|software.?studio/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

test('Admin CLI & Modelle toggles Codex to YOLO and the effective-mode probe reloads it', async ({ page }) => {
  const before = await api<CliModesResponse>(`/api/projects/${encodeURIComponent(projectName)}/cli-modes`);
  const originalOverride = before.overrides?.codex;

  await setCliMode(projectName, 'codex', 'read-only');

  try {
    await openAdminCliModes(page);

    const row = page.getByTestId('cli-mode-row-codex');
    await expect(row).toBeVisible();
    await expect(page.getByTestId('cli-mode-source-codex')).toHaveText(/project/i);
    await expect(page.getByTestId('cli-mode-select-codex')).toHaveValue('read-only');

    await page.getByTestId('cli-mode-select-codex').selectOption('yolo');

    await expect.poll(async () => effectiveMode(projectName, 'codex')).toMatchObject({
      cli: 'codex',
      project: projectName,
      mode: 'yolo',
      source: 'project',
      args: ['--sandbox', 'danger-full-access'],
    });

    await expect(page.getByTestId('cli-mode-select-codex')).toHaveValue('yolo');
    await expect(page.getByTestId('cli-mode-args-codex')).toContainText('--sandbox danger-full-access');
  } finally {
    await setCliMode(projectName, 'codex', originalOverride ?? '');
  }
});
