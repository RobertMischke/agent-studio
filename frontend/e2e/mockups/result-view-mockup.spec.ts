import { test, expect } from '@playwright/test';
import http from 'node:http';
import { AddressInfo } from 'node:net';
import path from 'node:path';
import fs from 'node:fs';

/**
 * Renders the REAL {@link ResultViewComponent} via the `result-view-mockup`
 * standalone app and screenshots the case-based overview layouts + the two new
 * compact quality-head metrics (files changed, tests passed) in BOTH themes.
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
const SCREENSHOT_PHASE = process.env.RESULT_VIEW_SCREENSHOT_PHASE?.trim() || 'after';

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
    test(`renders a compact result-summary row in the ${theme} theme`, async ({ page }) => {
      await page.setViewportSize({ width: 860, height: 1400 });
      await page.goto(baseUrl);
      await stampTheme(page, theme);

      const gallery = page.getByTestId('result-view-gallery');
      await expect(gallery).toBeVisible();
      // All case cards plus the operator-sighting density fixture render.
      await expect(page.getByTestId('gallery-card')).toHaveCount(5);
      // The quality-head metrics remain present after the density change.
      await expect(page.getByTestId('result-metric-files').first()).toBeVisible();
      await expect(page.getByTestId('result-metric-tests').first()).toBeVisible();
      // The per-case divergence: a blocker layout and a before-after layout exist.
      await expect(page.locator('[data-testid="result-overview"][data-layout="blocker"]')).toHaveCount(2);
      await expect(page.locator('[data-testid="result-overview"][data-layout="before-after"]')).toHaveCount(1);
      await expect(page.locator('[data-testid="result-overview"][data-layout="sequence"]')).toHaveCount(1);

      const operatorCard = page.locator('[data-gallery-card="operator-sighting"]');
      const summary = operatorCard.getByTestId('result-summary-meta');
      const outcome = operatorCard.getByTestId('result-case-badge');
      const duration = operatorCard.getByTestId('result-metric-duration');
      const files = operatorCard.getByTestId('result-metric-files');
      const tests = operatorCard.getByTestId('result-metric-tests');
      const tokens = operatorCard.getByTestId('result-metric-tokens');

      await expect(outcome).toHaveText('Pipeline failure');
      await expect(duration).toHaveText('20m');
      await expect(files).toHaveText('40 files');
      await expect(tests).toHaveText('81 ✓');
      await expect(tokens).toHaveText('72.9k tokens');
      await expect(operatorCard.getByTestId('result-outcome-dot')).toBeVisible();

      const density = await summary.evaluate((element) => {
        const outcomeElement = element.querySelector<HTMLElement>('[data-testid="result-case-badge"]')!;
        const metricElement = element.querySelector<HTMLElement>('[data-testid="result-metric-tests"]')!;
        const outcomeStyle = getComputedStyle(outcomeElement);
        const metricStyle = getComputedStyle(metricElement);
        return {
          height: element.getBoundingClientRect().height,
          scrollWidth: element.scrollWidth,
          clientWidth: element.clientWidth,
          outcomeBorder: outcomeStyle.borderStyle,
          outcomeBackground: outcomeStyle.backgroundColor,
          outcomeColor: outcomeStyle.color,
          metricBorder: metricStyle.borderStyle,
          metricBackground: metricStyle.backgroundColor,
          metricColor: metricStyle.color,
        };
      });
      expect(density.height).toBeLessThanOrEqual(24);
      expect(density.scrollWidth).toBeLessThanOrEqual(density.clientWidth);
      expect(density.outcomeBorder).toBe('none');
      expect(density.outcomeBackground).toBe('rgba(0, 0, 0, 0)');
      expect(density.metricBorder).toBe('none');
      expect(density.metricBackground).toBe('rgba(0, 0, 0, 0)');
      expect(density.outcomeColor).not.toBe(density.metricColor);

      await page.locator('[data-gallery-card="operator-sighting"]').screenshot({
        path: path.join(RESULTS_DIR, `result-summary-${SCREENSHOT_PHASE}-${theme}--mocked.png`),
      });
      await page.screenshot({
        path: path.join(RESULTS_DIR, `result-view-${theme}--mocked.png`),
        fullPage: true,
      });

      await page.setViewportSize({ width: 420, height: 900 });
      const narrowDensity = await summary.evaluate((element) => ({
        height: element.getBoundingClientRect().height,
        scrollWidth: element.scrollWidth,
        clientWidth: element.clientWidth,
      }));
      expect(narrowDensity.height).toBeLessThanOrEqual(44);
      expect(narrowDensity.scrollWidth).toBeLessThanOrEqual(narrowDensity.clientWidth);
      await operatorCard.screenshot({
        path: path.join(RESULTS_DIR, `result-summary-after-${theme}-narrow--mocked.png`),
      });
    });
  }
});
