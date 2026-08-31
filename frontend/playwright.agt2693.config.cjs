const { defineConfig, devices } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './e2e/task-detail',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: process.env.PW_BASE_URL || (process.env.PW_TARGET === 'stable'
      ? 'http://localhost:4011'
      : 'http://localhost:4010'),
    ...devices['Desktop Chrome'],
    channel: undefined,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  outputDir: 'test-results/agt2693',
});
