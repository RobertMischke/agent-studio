import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import type { Route } from '@playwright/test';
import { setTheme } from '../helpers/theme';
import { expect, test } from '../fixtures/dev-backend';

const RESULTS_DIR = process.env['JOB_RESULTS_DIR'] ?? '../results/status-bar';
mkdirSync(RESULTS_DIR, { recursive: true });

function json(body: unknown) {
  return (route: Route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

test.describe('local CLI self-heal status note', () => {
  test.use({ serviceWorkers: 'block' });

  test('renders repaired history calmly and makes only a failed repair acute', async ({ page, devBackend }) => {
    void devBackend;
    const occurredAt = new Date().toISOString();
    await page.route('**/api/auth/status', json({
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }));
    await page.route('**/api/cli/repairs/latest', json({
      cliType: 'claude',
      event: 'repair-succeeded',
      occurredAt,
      cliVersionBefore: '2.1.231',
      packageVersionBefore: '2.1.234',
      cliVersionAfter: '2.1.234',
      detail: 'claude npm shim restored; 2.1.231 -> 2.1.234.',
      journalPath: 'C:/workspace/logs/cli-self-heal.jsonl',
    }));

    await page.goto('/');
    const repaired = page.getByTestId('status-bar-cli-repair');
    await expect(repaired).toContainText('CLI repaired at');
    await expect(repaired).toHaveAttribute('data-signal-tone', 'calm');
    await expect(page.getByTestId('status-bar-cli-repair-divergence')).toHaveCount(0);

    await setTheme(page, 'light');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repaired-light--mocked.png'),
    });
    await setTheme(page, 'dark');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repaired-dark--mocked.png'),
    });

    await page.unroute('**/api/cli/repairs/latest');
    await page.route('**/api/cli/repairs/latest', json({
      cliType: 'codex',
      event: 'repair-failed',
      occurredAt,
      cliVersionBefore: '0.70.0',
      packageVersionBefore: '0.70.0',
      cliVersionAfter: null,
      detail: 'codex npm shim repair failed; CLI remains unavailable.',
      journalPath: 'C:/workspace/logs/cli-self-heal.jsonl',
    }));
    await page.reload();

    const failed = page.getByTestId('status-bar-cli-repair');
    await expect(failed).toContainText('CLI repair failed');
    await expect(failed).toHaveAttribute('data-signal-tone', 'mismatch');
    await expect(page.getByTestId('status-bar-cli-repair-divergence')).toHaveAccessibleName('CLI repair failed');
    await setTheme(page, 'light');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repair-failed-light--mocked.png'),
    });
    await setTheme(page, 'dark');
    await page.getByTestId('status-bar').screenshot({
      path: join(RESULTS_DIR, 'status-bar-cli-repair-failed-dark--mocked.png'),
    });
  });
});
