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

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

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

test.beforeAll(async () => {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThanOrEqual(1);
  const preferred = paths.find(p => /agent.?task|software.?studio/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

test('Project Settings toggles Codex to YOLO and the effective-mode probe reloads it', async ({ page }) => {
  const before = await api<CliModesResponse>(`/api/projects/${encodeURIComponent(projectName)}/cli-modes`);
  const originalOverride = before.overrides?.codex;

  await setCliMode(projectName, 'codex', 'read-only');

  try {
    await page.goto(`/#/projects/${slugFor(projectName)}/settings`);
    await expect(page.getByTestId('project-settings-panel')).toBeVisible({ timeout: 10_000 });

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
