import { test, expect } from '@playwright/test';
import http from 'node:http';
import { AddressInfo } from 'node:net';
import path from 'node:path';
import fs from 'node:fs';

/**
 * Renders the REAL {@link ResultViewComponent} via the `result-view-mockup`
 * standalone app and screenshots the case-based overview layouts + the two new
 * quality-head metric chips (files changed, tests passed) in BOTH themes.
 *
 * Backend-free by design: the gallery builds each card from a canned `status.md`
 * + task metadata, so this is the frontend verification the Teil 1 slice could
 * not run when the dev backend was offline. The shots carry the `--mocked`
 * source label (fixture data, no live backend), per protocol-style §4.4.
 *
 * Build the bundle first:  npm run build:mockup:result
 * Screenshots land in JOB_RESULTS_DIR when set (orchestrator runs), otherwise
 * in docs/mockups/result-view/evidence/ for local inspection.
 */

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'result-view-mockup', 'browser');
const RESULTS_DIR =
  process.env.JOB_RESULTS_DIR?.trim() ||
  path.resolve(__dirname, '..', '..', '..', 'docs', 'mockups', 'result-view', 'evidence');

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.webmanifest': 'application/manifest+json; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
};

let server: http.Server;
let baseUrl: string;

test.beforeAll(async () => {
  if (!fs.existsSync(path.join(DIST_DIR, 'index.html'))) {
    throw new Error(
      `Missing build at ${DIST_DIR}. Run "npm run build:mockup:result" before this spec.`,
    );
  }
  fs.mkdirSync(RESULTS_DIR, { recursive: true });

  server = http.createServer((req, res) => {
    const urlPath = decodeURIComponent((req.url ?? '/').split('?')[0]);
    let filePath = path.join(DIST_DIR, urlPath === '/' ? 'index.html' : urlPath);
    if (!fs.existsSync(filePath) && !path.extname(filePath)) {
      filePath = path.join(DIST_DIR, 'index.html');
    }
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

async function stampTheme(page: import('@playwright/test').Page, theme: 'dark' | 'light') {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
  }, theme);
}

test.describe('@mockup result-view (real component)', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`renders the case layouts + metric chips in the ${theme} theme`, async ({ page }) => {
      await page.setViewportSize({ width: 860, height: 1400 });
      await page.goto(baseUrl);
      await stampTheme(page, theme);

      const gallery = page.getByTestId('result-view-gallery');
      await expect(gallery).toBeVisible();
      // All four case cards render.
      await expect(page.getByTestId('gallery-card')).toHaveCount(4);
      // The two new quality-head chips are present.
      await expect(page.getByTestId('result-metric-files').first()).toBeVisible();
      await expect(page.getByTestId('result-metric-tests').first()).toBeVisible();
      // The per-case divergence: a blocker layout and a before-after layout exist.
      await expect(page.locator('[data-testid="result-overview"][data-layout="blocker"]')).toHaveCount(1);
      await expect(page.locator('[data-testid="result-overview"][data-layout="before-after"]')).toHaveCount(1);
      await expect(page.locator('[data-testid="result-overview"][data-layout="sequence"]')).toHaveCount(1);

      await page.screenshot({
        path: path.join(RESULTS_DIR, `result-view-${theme}--mocked.png`),
        fullPage: true,
      });
    });
  }
});
