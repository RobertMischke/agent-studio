import { expect, test } from '@playwright/test';
import http from 'node:http';
import type { AddressInfo } from 'node:net';
import fs from 'node:fs';
import path from 'node:path';

/**
 * Renders the real PlanningSpawnPanelComponent with fixture-backed planning
 * summaries. The mockup keeps visual verification deterministic while the
 * component, its responsive CSS, and both theme token sets are real.
 *
 * Build first with: npm run build:mockup:planning -- --configuration development
 */

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'planning-visibility-mockup', 'browser');
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  || path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'planning-visibility');

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
};

let server: http.Server;
let baseUrl: string;

test.beforeAll(async () => {
  if (!fs.existsSync(path.join(DIST_DIR, 'index.html'))) {
    throw new Error(`Missing ${DIST_DIR}. Run "npm run build:mockup:planning -- --configuration development" first.`);
  }
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  server = http.createServer((req, res) => {
    const requestPath = decodeURIComponent((req.url ?? '/').split('?')[0]);
    const filePath = path.join(DIST_DIR, requestPath === '/' ? 'index.html' : requestPath);
    if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
      res.statusCode = 404;
      res.end('not found');
      return;
    }
    res.setHeader('Content-Type', MIME[path.extname(filePath)] ?? 'application/octet-stream');
    fs.createReadStream(filePath).pipe(res);
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as AddressInfo;
  baseUrl = `http://127.0.0.1:${port}/`;
});

test.afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

test.describe('@mockup planning follow-up status', () => {
  test('shows one compact message with both actions in both themes and a narrow panel', async ({ page }) => {
    await page.setViewportSize({ width: 720, height: 900 });
    await page.goto(baseUrl);

    const scenario = page.locator('[data-scenario="at-risk"]');
    const panel = scenario.getByTestId('planning-spawn-panel');
    const status = panel.getByTestId('planning-no-followups-status');
    await expect(panel).toBeVisible();
    await expect(status).toHaveText('No follow-up cards created');
    await expect(scenario.getByTestId('planning-contract')).toHaveCount(0);
    await expect(panel.getByRole('button', { name: 'Promote to coding task' })).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Declare: no follow-up intended' })).toBeVisible();

    const messageMatches = (await panel.innerText()).match(/No follow-up cards created/g) ?? [];
    expect(messageMatches).toHaveLength(1);

    for (const theme of ['light', 'dark'] as const) {
      await page.getByTestId(`harness-theme-${theme}`).click();
      if (theme === 'light') {
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');
      } else {
        await expect(page.locator('html')).not.toHaveAttribute('data-studio-theme', /.+/);
      }
      await panel.screenshot({
        path: path.join(RESULTS_DIR, `planning-spawn-panel--${theme}--mocked.png`),
      });
    }

    await page.setViewportSize({ width: 320, height: 800 });
    await page.getByTestId('harness-theme-light').click();
    await expect.poll(async () => panel.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
    await panel.screenshot({
      path: path.join(RESULTS_DIR, 'planning-spawn-panel--narrow-light--mocked.png'),
    });
  });
});
