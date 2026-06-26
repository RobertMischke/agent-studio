/**
 * Pins the session->task chip on the Orchestrator -> Sessions panel:
 *
 *   1. When the backend reports a `linkedJob` whose owning task is currently
 *      active (lane=3-progress and `isActive: true`), the chip renders with
 *      `data-state="active"`.
 *   2. When the report later returns the same row with `isActive: false`,
 *      the chip flips to `data-state="linked"`.
 *   3. Clicking the chip emits an open-task event that the shell handles
 *      via the existing `openJobDetail` flow, dropping the user on the
 *      kanban detail panel for the owning task.
 *
 * No CLI spawn: the `/api/cli/usage` response is route-mocked so the test
 * does not pay billable quota. The backend's index correctness is already
 * covered by `SessionToJobIndexTests` in the .NET test project.
 *
 * The spec uses the `dev-backend` fixture so it can run from stable's
 * Playwright suite against the dev backend, per the Playwright-only dev
 * backend rule in AGENTS.md.
 */
import { test, expect } from '../fixtures/dev-backend';
import type { Route } from '@playwright/test';
import { createJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string }

const FAKE_SESSION_ID = '11111111-2222-3333-4444-555555555555';

function buildUsageReport(
  watchPath: string,
  projectName: string,
  jobId: string,
  jobTitle: string,
  isActive: boolean
) {
  return {
    at: new Date().toISOString(),
    sections: [
      {
        cliType: 'claude',
        available: true,
        version: 'stub-1.0',
        path: '/usr/local/bin/claude',
        error: null,
        projects: [
          {
            projectName,
            rootPath: watchPath,
            sessions: [
              {
                id: FAKE_SESSION_ID,
                label: FAKE_SESSION_ID.slice(0, 8),
                updatedAt: new Date().toISOString(),
                cwd: watchPath,
                lastUsage: null,
                isProjectDefault: false,
                linkedJob: {
                  jobId,
                  title: jobTitle,
                  watchPath,
                  projectName,
                  lane: isActive ? '3-progress' : '5-human-review',
                  isActive
                }
              }
            ]
          }
        ]
      },
      { cliType: 'copilot', available: true, version: null, path: '/usr/local/bin/copilot', error: null, projects: [] },
      { cliType: 'codex',   available: true, version: null, path: '/usr/local/bin/codex', error: null, projects: [] },
      { cliType: 'gemini',  available: true, version: null, path: '/usr/local/bin/gemini', error: null, projects: [] }
    ]
  };
}

test('session->task chip transitions active -> linked and routes click', async ({ page, devBackend }) => {
  // We need a real job in dev's workspace so the click handler's
  // openDetail call can resolve a real JobDetail. The job lives in
  // human-review (terminal-ish) so it does not flicker through lanes
  // while the test runs.
  const paths = await fetch(`${devBackend.baseUrl}/api/watch-paths`).then(r => r.json()) as WatchPath[];
  const wp = paths.find(p => p.path && p.name) ?? paths[0];
  if (!wp) test.skip(true, 'no watch path on dev backend');

  const jobTitle = `session-link-chip-${Date.now()}`;
  const { id: jobId } = await createJob({
    title: jobTitle,
    watchPath: wp!.path,
    cliType: 'claude',
    agent: 'claude',
    targetState: '5-human-review'
  });

  // Drive the mock through a single boolean so both states reuse one route handler.
  let activeFlag = true;
  let usageCalls = 0;
  await page.route('**/api/cli/usage', async (route: Route) => {
    usageCalls += 1;
    const body = buildUsageReport(wp!.path, wp!.name, jobId, jobTitle, activeFlag);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body)
    });
  });

  try {
    await page.goto('/');

    // Open the Orchestrator side sheet (left rail) and switch to Sessions.
    // Orchestrator toggles vary by theme but the trigger always has the
    // word "Orchestrator" or a robot icon. The chat panel is the default.
    const orchestratorButton = page.getByRole('button', { name: /orchestrator/i }).first();
    await orchestratorButton.click({ trial: false });

    // Navigate to the Sessions tab inside the orchestrator side sheet.
    const sessionsTab = page.getByRole('button', { name: /^sessions$/i }).first();
    await sessionsTab.click();

    const chip = page.getByTestId('session-task-link');
    await chip.waitFor({ state: 'visible', timeout: 15_000 });
    await expect(chip).toHaveAttribute('data-state', 'active');

    // Flip the mock and force a refresh: the panel exposes a Refresh button.
    activeFlag = false;
    await page.getByRole('button', { name: /refresh/i }).first().click();

    // Chip text + data-state should update on the next /api/cli/usage poll.
    await expect.poll(async () => chip.getAttribute('data-state'), { timeout: 10_000 })
      .toBe('linked');

    // Click the chip and assert the kanban detail panel for the owning task opens.
    await chip.click();
    await expect.poll(() => page.url(), { timeout: 5_000 })
      .toContain(`job=${encodeURIComponent(jobId)}`);

    expect(usageCalls).toBeGreaterThan(0);
  } finally {
    // Cleanup: fixtures (fixture:true) are filtered out of the default
    // kanban response, but deleting keeps the watch-path tree tidy.
    await fetch(
      `${devBackend.baseUrl}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(wp!.path)}`,
      { method: 'DELETE', headers: { 'x-client-id': 'local-default' } }
    ).catch(() => {});
  }
});
