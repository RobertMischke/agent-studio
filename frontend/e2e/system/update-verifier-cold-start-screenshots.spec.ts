import { test, type Page, type Route } from '@playwright/test';

/**
 * Job-evidence screenshots for the cold-start verifier window fix
 * (`human-decision-needed-bug-update-verifier-window-too-short-for-
 * cold-start`). Captures the still-draining toast wording in both
 * dark and light themes so the reviewer can confirm WCAG-AA contrast
 * at a glance.
 *
 * The toast reuses the unified <app-notification kind="error"> primitive,
 * whose dark+light contrast is already locked by f40-update-banner-
 * themes.spec.ts; this spec just renders the new wording in situ. Skipped
 * outside JOB_RESULTS_DIR runs so the regular suite stays quick.
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

const stillDrainingStatus: MockStatus = {
  phase: 'failed',
  phaseLabel: 'Update failed',
  message: 'verification failed: jobs-grouped',
  currentRunId: 'cold-evidence-1',
  isRunning: false,
  backendReachable: true,
  headLocal: 'bbbbbbb',
  headOrigin: 'bbbbbbb',
  behindBy: 0,
  pendingCommits: [],
  startedAt: null,
  finishedAt: null,
  lastFetchAt: null,
  lastUpdateAt: null,
  lastSuccessAt: null,
  lastRunFinishedAt: new Date().toISOString(),
  lastRunHeadBefore: 'aaaaaaa',
  lastRunHeadAfter: 'bbbbbbb',
  serviceVersion: '0.0.0',
  productVersion: '0.1.0',
  mode: 'manual',
  verificationFailures: [
    {
      step: 'jobs-grouped',
      observed: 'no response (backend still starting up; healthz=200 but endpoint did not drain in time)',
      expected: 'http=200',
    },
  ],
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

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

const EVIDENCE_DIR = process.env.JOB_RESULTS_DIR ?? 'test-results/_evidence/cold-start';

test.describe('Cold-start verifier toast evidence', () => {
  test.skip(!process.env.JOB_RESULTS_DIR, 'evidence-only; gated on JOB_RESULTS_DIR.');

  for (const theme of ['dark', 'light'] as const) {
    test(`still-draining toast renders in ${theme} theme`, async ({ page }) => {
      await installUpdateMock(page, () => stillDrainingStatus);
      await page.goto('/', { waitUntil: 'domcontentloaded' });
      await setTheme(page, theme);
      // Re-navigate so the theme attribute applies before app-shell paints
      // its toast layer.
      await page.goto('/', { waitUntil: 'domcontentloaded' });
      const toast = page.locator('[data-testid="notification-error"]').first();
      await toast.waitFor({ state: 'visible', timeout: 10_000 });
      await page.screenshot({
        path: `${EVIDENCE_DIR}/still-draining-toast-${theme}.png`,
        fullPage: false,
      });
    });
  }
});
