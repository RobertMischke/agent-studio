import { test, expect, Page, Route } from '@playwright/test';

/**
 * Regression coverage for the false "Update failed" toast that the operator
 * hit on 2026-05-28 (`human-decision-needed-bug-update-verifier-window-too-
 * short-for-cold-start`). F58 had given the post-restart /api/jobs/grouped
 * probe a 15 s retry window; a cold-started backend with several hundred job
 * folders routinely takes 60-120 s to drain, so the verifier flagged
 * "failed" while healthz was already green and the FE offered a roll back.
 *
 * Backend side: `UpdateVerifier.CheckJobsGroupedAsync` now retries for up to
 * ~120 s and tags the observed text "still starting up" when /healthz was
 * alive across the wait. This spec locks the FE contract that consumes
 * that signal:
 *
 *   1. A failed status whose observed text contains "still starting up"
 *      renders a less alarming "still starting up" toast wording instead
 *      of the generic "no response" copy.
 *   2. When the same runId later flips to `phase=done` (e.g. the backend
 *      drained right after the verdict was written), the toast must
 *      upgrade in place from the failed state to the success state.
 *
 * All UpdateService traffic is mocked via `page.route`; no port-5039
 * process is required.
 */

interface MockStatus {
  phase: string;
  phaseLabel: string | null;
  message: string | null;
  currentRunId: string | null;
  isRunning: boolean;
  backendReachable: boolean;
  headLocal: string;
  headOrigin: string | null;
  behindBy: number;
  pendingCommits: unknown[];
  startedAt: string | null;
  finishedAt: string | null;
  lastFetchAt: string | null;
  lastUpdateAt: string | null;
  lastSuccessAt: string | null;
  lastRunFinishedAt: string | null;
  lastRunHeadBefore: string | null;
  lastRunHeadAfter: string | null;
  serviceVersion: string;
  productVersion: string;
  mode: 'manual' | 'scheduled';
  verificationFailures: Array<{ step: string; observed: string; expected: string }> | null;
  autoRollbackEnabled: boolean;
}

const baseStatus: MockStatus = {
  phase: 'idle',
  phaseLabel: null,
  message: null,
  currentRunId: null,
  isRunning: false,
  backendReachable: true,
  headLocal: 'aaaaaaa',
  headOrigin: 'aaaaaaa',
  behindBy: 0,
  pendingCommits: [],
  startedAt: null,
  finishedAt: null,
  lastFetchAt: null,
  lastUpdateAt: null,
  lastSuccessAt: null,
  lastRunFinishedAt: null,
  lastRunHeadBefore: null,
  lastRunHeadAfter: null,
  serviceVersion: '0.0.0',
  productVersion: '0.1.0',
  mode: 'manual',
  verificationFailures: null,
  autoRollbackEnabled: false,
};

async function installUpdateMock(page: Page, getStatus: () => MockStatus) {
  await page.route('**://*:5039/**', async (route: Route) => {
    const url = route.request().url();
    if (url.endsWith('/healthz') || url.endsWith('/update/health')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '"ok"' });
      return;
    }
    if (url.includes('/update/status')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(getStatus()) });
      return;
    }
    if (url.includes('/update/history')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
  });
}

test.describe('Update verifier cold-start toast (mocked)', () => {
  test('still-starting-up observed text renders the soft "still draining" copy', async ({ page }) => {
    // Mimics what the backend now emits when healthz answered 200 across
    // the retry loop but /api/jobs/grouped did not drain inside the 120 s
    // window. The bridge must NOT use the alarmist roll-back wording.
    const status: MockStatus = {
      ...baseStatus,
      phase: 'failed',
      phaseLabel: 'Update failed',
      message: 'verification failed: jobs-grouped',
      currentRunId: 'cold-1',
      isRunning: false,
      lastRunFinishedAt: new Date().toISOString(),
      lastRunHeadBefore: 'aaaaaaa',
      lastRunHeadAfter: 'bbbbbbb',
      headLocal: 'bbbbbbb',
      verificationFailures: [
        {
          step: 'jobs-grouped',
          observed: 'no response (backend still starting up; healthz=200 but endpoint did not drain in time)',
          expected: 'http=200',
        },
      ],
    };
    await installUpdateMock(page, () => status);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const errorToast = page.locator('[data-testid="notification-error"]').first();
    await expect(errorToast, 'error toast should appear for the failed phase').toBeVisible({ timeout: 10_000 });
    const message = errorToast.locator('[data-testid="notification-message"]');
    await expect(message).toContainText('still starting up');
    // The cold-start wording deliberately steers operators away from
    // rolling back: rolling back a backend that is merely still draining
    // throws away progress and re-incurs the same drain on the rollback
    // run. The wording must mention waiting/retry, not just rolling back.
    await expect(message).toContainText(/wait|retry/i);
  });

  test('done state for the same runId upgrades a previously shown failed toast', async ({ page }) => {
    // Belt-and-suspenders for the cold-start drain: if the verifier
    // happens to write "failed" right before the backend answers and the
    // status later flips to "done" for the same runId, the operator must
    // see the success toast and not be parked on the alarming red one.
    //
    // Timing note: UpdateClientService polls /update/status every 30 s
    // while idle and every 2 s while a run is in flight. The failed
    // snapshot below leaves isRunning=false, so we hold isRunning=true on
    // the next poll to drop the cadence to 2 s before settling on done.
    // The test budget is therefore ~50 s; bump the spec timeout
    // accordingly so we are not racing the default 60 s ceiling on a
    // slow box.
    test.setTimeout(90_000);

    let status: MockStatus = {
      ...baseStatus,
      phase: 'failed',
      phaseLabel: 'Update failed',
      message: 'verification failed: jobs-grouped',
      currentRunId: 'flip-1',
      isRunning: false,
      lastRunFinishedAt: new Date().toISOString(),
      lastRunHeadBefore: 'aaaaaaa',
      lastRunHeadAfter: 'bbbbbbb',
      headLocal: 'bbbbbbb',
      verificationFailures: [
        {
          step: 'jobs-grouped',
          observed: 'no response (backend still starting up; healthz=200 but endpoint did not drain in time)',
          expected: 'http=200',
        },
      ],
    };
    await installUpdateMock(page, () => status);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const errorToast = page.locator('[data-testid="notification-error"]').first();
    await expect(errorToast).toBeVisible({ timeout: 10_000 });

    // Phase 1 of the flip: switch to isRunning=true for one tick so the
    // FE drops to 2 s polling cadence. The bridge skips isRunning=true
    // statuses, so this does NOT mutate lastHandledRunId / lastHandledPhase.
    status = { ...status, isRunning: true, phase: 'verifying-after-restart' };

    // Phase 2: settle on done with the same runId. The bridge's
    // failed-to-done upgrade path must fire because lastHandledRunId
    // still matches and lastHandledPhase is still "failed".
    await page.waitForTimeout(35_000);
    status = {
      ...baseStatus,
      phase: 'done',
      phaseLabel: 'Update verified',
      currentRunId: 'flip-1',
      isRunning: false,
      lastRunFinishedAt: new Date().toISOString(),
      lastRunHeadBefore: 'aaaaaaa',
      lastRunHeadAfter: 'bbbbbbb',
      headLocal: 'bbbbbbb',
    };

    const successToast = page.locator('[data-testid="notification-success"]').first();
    await expect(successToast, 'success toast should appear after the flip').toBeVisible({ timeout: 15_000 });
    // The previous failed toast must be gone (defense-in-depth: a stuck
    // "Update failed" beside the new "Update finished" is the regression).
    await expect(errorToast).not.toBeVisible();
  });
});
