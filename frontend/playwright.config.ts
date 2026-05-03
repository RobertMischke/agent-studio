import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright configuration for Agent Task Processor frontend E2E tests.
 *
 * Conventions (read this before writing tests):
 * - Tests assume the target stack is already running:
 *     - DEV (default): backend 5030 / frontend 4010 (start with `.\api.ps1 start`)
 *     - STABLE: backend 5031 / frontend 4011 (start with `start-stable.sh`)
 *   We do NOT spawn them via `webServer` because the user keeps long-lived
 *   instances open during development. Tests fail fast if either is down.
 * - Choose target via env vars (in order of precedence):
 *     - PW_BASE_URL=http://...    explicit override
 *     - PW_TARGET=stable          http://localhost:4011
 *     - PW_TARGET=dev   (default) http://localhost:4010
 *   Agents driving Playwright as User should set `PW_TARGET=stable` so their
 *   tests never collide with `dotnet build` locks on the dev backend's bin
 *   folder while a feature is being developed.
 * - Single-browser target (chromium headless-shell) to keep the install small.
 * - Use stable selectors: prefer `data-testid="..."`, then role/label, then text.
 *   Never select by CSS class — they change with styling work.
 * - Tests must clean up any state they create (jobs, sessions). See
 *   `e2e/helpers/jobs.ts` for the API-level cleanup helpers.
 * - JobArtifactReporter activates when JOB_RESULTS_DIR env var is set (agent task orchestrator).
 *   It harvests test artifacts into <JOB_RESULTS_DIR>/playwright/ with summary index.json.
 *   Not used during local development.
 */
const pwTarget = (process.env.PW_TARGET ?? 'dev').toLowerCase();
const resolvedBaseUrl =
  process.env.PW_BASE_URL?.trim()
  || (pwTarget === 'stable' ? 'http://localhost:4011' : 'http://localhost:4010');

const reporters: any[] = [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]];
// Activate job artifact reporter only when JOB_RESULTS_DIR is set (agent task orchestrator mode).
if (process.env.JOB_RESULTS_DIR) {
  reporters.push(['./e2e/helpers/job-artifact-reporter.ts']);
}

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: 1,
  reporter: reporters,
  use: {
    baseURL: resolvedBaseUrl,
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
