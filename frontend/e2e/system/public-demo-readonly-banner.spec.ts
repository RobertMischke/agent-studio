import { expect } from '@playwright/test';
import { test } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

/**
 * W34 §8 S4 "Public read-only edge". The backend edge
 * (PublicDemoEdgeMiddleware) is the real security boundary; this only
 * verifies the explanatory UX the dossier calls for: a visible banner driven
 * by GET /api/environment's publicDemo flag. The client-side mutation block
 * (publicDemoGuardInterceptor) has its own HttpTestingController coverage in
 * public-demo-guard.interceptor.spec.ts.
 *
 * The dev backend always runs the local profile, so the one endpoint that
 * carries the public-demo flag (/api/environment) is mocked at the browser
 * network layer; every other call (auth/status, board data, hubs) hits the
 * real dev backend the frontend proxies to.
 */
test.describe('Public demo read-only banner', () => {
  test('renders when the backend reports the public-demo-readonly profile', async ({ page, devBackend }) => {
    void devBackend;
    await page.route('**/api/environment', (route) => route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
        publicDemo: { active: true, profile: 'public-demo-readonly' },
      }),
    }));

    await page.goto('/');

    const banner = page.getByTestId('public-demo-banner');
    await expect(banner).toBeVisible();
    await expect(banner).toContainText('Read-only public demo');
    const box = await banner.boundingBox();
    expect(box?.width, 'the read-only banner spans the viewport top edge').toBeGreaterThan(400);
    await expect(page.getByTestId('studio-board-add-task')).toBeDisabled();

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: `test-results/public-demo-banner--${theme}--mocked.png`,
        fullPage: false,
      });
    }
  });

  test('stays hidden when the backend is not in the public-demo profile', async ({ page, devBackend }) => {
    void devBackend;
    await page.route('**/api/environment', (route) => route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
        publicDemo: { active: false, profile: 'local' },
      }),
    }));

    await page.goto('/');

    await expect(page.getByTestId('public-demo-banner')).toHaveCount(0);
  });
});
