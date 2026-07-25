import type { Page } from '@playwright/test';

/**
 * Keep the browser on the operator-provided Studio origin while serving a
 * changed worktree's frontend bundle for post-fix verification. API and hub
 * traffic always stays on the configured Playwright base URL.
 */
export async function installFrontendOverride(page: Page): Promise<void> {
  const override = process.env['PW_FRONTEND_OVERRIDE']?.trim().replace(/\/$/, '');
  if (!override) return;

  await page.route('**/*', async route => {
    const requestUrl = new URL(route.request().url());
    if (requestUrl.pathname.startsWith('/api/') || requestUrl.pathname.startsWith('/hubs/')) {
      await route.fallback();
      return;
    }

    const targetUrl = new URL(`${requestUrl.pathname}${requestUrl.search}`, override);
    const response = await route.fetch({ url: targetUrl.toString() });
    await route.fulfill({ response });
  });
}
