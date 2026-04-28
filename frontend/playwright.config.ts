import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright configuration for Agent Task Processor frontend E2E tests.
 *
 * Conventions (read this before writing tests):
 * - Tests assume the dev stack is already running:
 *     - Backend at http://localhost:5030 (start with `.\api.ps1 start`)
 *     - Frontend at http://localhost:4010 (start with `npm start --prefix frontend`)
 *   We do NOT spawn them via `webServer` because the user keeps long-lived
 *   instances open during development. Tests fail fast if either is down.
 * - Single-browser target (chromium headless-shell) to keep the install small.
 * - Use stable selectors: prefer `data-testid="..."`, then role/label, then text.
 *   Never select by CSS class — they change with styling work.
 * - Tests must clean up any state they create (jobs, sessions). See
 *   `e2e/helpers/jobs.ts` for the API-level cleanup helpers.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: 'http://localhost:4010',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 10_000,
    navigationTimeout: 15_000
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'], channel: undefined } }
  ],
  outputDir: 'test-results'
});
