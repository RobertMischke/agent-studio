// T6a — backend-free screenshot capture for the Workflow / Lanes stage-1 page.
//
// Self-contained: builds nothing (run `ng build workflow-lanes-mockup` first),
// serves the built bundle from dist/workflow-lanes-mockup over a tiny static
// HTTP server with SPA fallback, then drives chromium to capture one labelled
// `--mocked` screenshot of the shipped read-only transparency surface.
//
// The page is rendered from a stub TaskService (see the gallery component), so
// no platform backend is started and no shared workspace state is touched —
// the same precedent as the other src/mockups/* harnesses.
//
// Usage: node scripts/workflow-lanes-t6a-shots.mjs <outDir> [port]
import { chromium } from '@playwright/test';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { existsSync, mkdirSync } from 'node:fs';
import { join, extname, resolve } from 'node:path';

const outDir = process.argv[2];
const port = Number(process.argv[3] || 4027);
if (!outDir) {
  console.error('usage: node scripts/workflow-lanes-t6a-shots.mjs <outDir> [port]');
  process.exit(2);
}
mkdirSync(outDir, { recursive: true });

// @angular/build emits the browser bundle under a `browser/` subdir.
const candidates = [resolve('dist/workflow-lanes-mockup/browser'), resolve('dist/workflow-lanes-mockup')];
const distDir = candidates.find((d) => existsSync(join(d, 'index.html')));
if (!distDir) {
  console.error(`missing build output: dist/workflow-lanes-mockup[/browser]/index.html — run "ng build workflow-lanes-mockup" first`);
  process.exit(3);
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.map': 'application/json; charset=utf-8',
};

const server = createServer(async (req, res) => {
  try {
    const urlPath = decodeURIComponent((req.url || '/').split('?')[0]);
    let filePath = join(distDir, urlPath === '/' ? 'index.html' : urlPath);
    if (!existsSync(filePath)) filePath = join(distDir, 'index.html'); // SPA fallback
    const body = await readFile(filePath);
    res.writeHead(200, { 'content-type': MIME[extname(filePath)] || 'application/octet-stream' });
    res.end(body);
  } catch (err) {
    res.writeHead(500);
    res.end(String(err));
  }
});

await new Promise((r) => server.listen(port, '127.0.0.1', r));
const base = `http://127.0.0.1:${port}/`;
console.log(`serving ${distDir} at ${base}`);

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 1100 }, deviceScaleFactor: 2 });
await page.goto(base, { waitUntil: 'networkidle' });

// Wait for the read-only transparency surface to render from the stub settings.
await page.waitForSelector('[data-testid="workflow-lane-list"]');
await page.waitForSelector('[data-testid="workflow-transitions"]');
// Settings resolved, not the loading dash.
await page.waitForFunction(() => {
  const el = document.querySelector('[data-testid="workflow-transition-state-auto-commit"]');
  return !!el && el.textContent && el.textContent.trim() !== '…';
});
await page.waitForTimeout(300); // let ngModel flush its initial select value

const shot = join(outDir, 'workflow-lanes-stage1--mocked.png');
await page.screenshot({ path: shot, fullPage: true });

// Log the rendered transition state strings so the evidence is self-describing.
for (const key of ['auto-commit', 'attribution', 'gates', 'auto-push']) {
  const txt = await page
    .locator(`[data-testid="workflow-transition-state-${key}"]`)
    .innerText()
    .catch(() => '(none)');
  console.log(`transition ${key} -> "${txt.trim()}"`);
}
const laneCount = await page.locator('[data-testid="workflow-lane-list"] [data-testid^="workflow-lane-"]').count();
console.log(`lane rows: ${laneCount}`);

await browser.close();
await new Promise((r) => server.close(r));
console.log(`screenshot written: ${shot}`);
