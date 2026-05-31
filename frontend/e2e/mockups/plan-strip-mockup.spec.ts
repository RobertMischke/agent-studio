import { test, expect } from '@playwright/test';
import http from 'node:http';
import { AddressInfo } from 'node:net';
import path from 'node:path';
import fs from 'node:fs';

/**
 * Renders the REAL PlanStripComponent (not a click-dummy) via the
 * `plan-strip-mockup` standalone app and captures screenshots of the four
 * progress cues - ticker, latest label, soft-estimate band, heartbeat - in
 * both the live-run and finished-run states.
 *
 * Build the bundle first:  npm run build:mockup:plan
 * Screenshots land in JOB_RESULTS_DIR when set (orchestrator runs), otherwise
 * in docs/mockups/task-progress-tracking/evidence/ for local inspection.
 */

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'plan-strip-mockup', 'browser');
const RESULTS_DIR =
  process.env.JOB_RESULTS_DIR?.trim() ||
  path.resolve(__dirname, '..', '..', '..', 'docs', 'mockups', 'task-progress-tracking', 'evidence');

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
      `Missing build at ${DIST_DIR}. Run "npm run build:mockup:plan" before this spec.`
    );
  }
  fs.mkdirSync(RESULTS_DIR, { recursive: true });

  server = http.createServer((req, res) => {
    const urlPath = decodeURIComponent((req.url ?? '/').split('?')[0]);
    let filePath = path.join(DIST_DIR, urlPath === '/' ? 'index.html' : urlPath);
    // SPA fallback: unknown routes (no file extension) serve index.html.
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

test.describe('@mockup plan-strip (real component)', () => {
  test('captures the four progress cues in live and finished states', async ({ page }) => {
    await page.setViewportSize({ width: 760, height: 1100 });
    await page.goto(baseUrl);

    const activeSection = page.getByTestId('gallery-active');
    const doneSection = page.getByTestId('gallery-done');
    await expect(activeSection.getByTestId('plan-strip')).toBeVisible();
    await expect(doneSection.getByTestId('plan-strip')).toBeVisible();

    // --- Live run: all four cues on the active item. ---
    const activeItem = activeSection.locator('[data-testid="plan-item"][data-status="active"]');
    await expect(activeItem).toHaveCount(1);
    // Cue 1: ticker marks. Cue 2: latest label. Cue 3: soft-estimate band.
    // Cue 4: heartbeat pulsing (fixture timestamps are seconds old).
    await expect(activeItem.getByTestId('plan-item-ticker')).toBeVisible();
    await expect(activeItem.getByTestId('plan-item-latest')).toBeVisible();
    await expect(activeItem.getByTestId('plan-item-band')).toHaveText('~3');
    await expect(activeItem.getByTestId('plan-item-heartbeat')).toHaveAttribute(
      'data-state',
      'pulsing'
    );
    // The source badge must name the CLI honestly (no "heuristic" tag here).
    await expect(activeSection.getByTestId('plan-strip-source')).toHaveText('Claude');
    await expect(activeSection.getByTestId('plan-strip-count')).toHaveText('2/5 done');

    await activeSection.getByTestId('plan-strip').screenshot({
      path: path.join(RESULTS_DIR, 'real-01-live-run-four-cues.png'),
    });

    // --- Finished run: every item expands to its verbatim sub-action list. ---
    await expect(doneSection.getByTestId('plan-strip-source')).toHaveText('Codex');
    const expandButtons = doneSection.getByTestId('plan-item-expand');
    const count = await expandButtons.count();
    expect(count).toBeGreaterThan(0);
    for (let i = 0; i < count; i++) {
      await expandButtons.nth(i).click();
    }
    await expect(doneSection.getByTestId('plan-item-subs').first()).toBeVisible();

    await doneSection.getByTestId('plan-strip').screenshot({
      path: path.join(RESULTS_DIR, 'real-02-finished-expanded.png'),
    });

    // --- Full gallery overview for the chat reply. ---
    await page.screenshot({
      path: path.join(RESULTS_DIR, 'real-03-gallery-overview.png'),
      fullPage: true,
    });
  });
});
