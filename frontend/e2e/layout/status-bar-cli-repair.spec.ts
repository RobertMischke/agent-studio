import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env['JOB_RESULTS_DIR'] ?? '../results/status-bar';
mkdirSync(RESULTS_DIR, { recursive: true });

function capabilityReport(outcome: 'succeeded' | 'failed') {
  return {
    at: '2026-08-18T14:05:00Z',
    capabilities: [
      { cliType: 'claude', state: outcome === 'succeeded' ? 'available' : 'missing-shim-with-package-present',
        available: outcome === 'succeeded', cliVersion: outcome === 'succeeded' ? '2.1.234' : null,
        packageVersion: '2.1.234', executablePath: 'claude' },
    ],
    latestRepair: {
      at: '2026-08-18T14:05:00Z',
      cliType: 'claude',
      outcome,
      cliVersionBefore: null,
      packageVersionBefore: '2.1.231',
      cliVersionAfter: outcome === 'succeeded' ? '2.1.234' : null,
      packageVersionAfter: outcome === 'succeeded' ? '2.1.234' : '2.1.231',
      error: outcome === 'failed' ? 'npm install exited 1' : null,
    },
  };
}

test.describe('local CLI repair status note', () => {
  test('renders successful repair quietly in both themes', async ({ page, devBackend }) => {
    void devBackend;
    await page.route('**/api/cli/local-capabilities', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(capabilityReport('succeeded')),
    }));

    await page.goto('/');
    const note = page.getByTestId('status-bar-cli-repair');
    await expect(note).toContainText('CLI repaired at');
    await expect(note).toHaveAttribute('data-signal-tone', 'calm');

    await setTheme(page, 'dark');
    await note.screenshot({ path: join(RESULTS_DIR, 'status-bar-cli-repair-dark--mocked.png') });
    await setTheme(page, 'light');
    await note.screenshot({ path: join(RESULTS_DIR, 'status-bar-cli-repair-light--mocked.png') });
  });

  test('uses the acute signal only when repair fails', async ({ page, devBackend }) => {
    void devBackend;
    await page.route('**/api/cli/local-capabilities', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(capabilityReport('failed')),
    }));

    await page.goto('/');
    const note = page.getByTestId('status-bar-cli-repair');
    await expect(note).toContainText('CLI repair failed at');
    await expect(note).toHaveAttribute('data-signal-tone', 'mismatch');
  });
});
