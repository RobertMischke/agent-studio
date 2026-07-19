// ASS-1665 — backend-free screenshot capture for the git-state pill gallery.
//
// Self-contained: run `ng build git-state-pill-mockup` first, then this script
// serves the built bundle from dist/git-state-pill-mockup over a tiny static
// HTTP server with SPA fallback and drives chromium to capture one labelled
// `--mocked` screenshot. The pill is a pure function of TaskInfo (seeded jobs in
// the gallery component), so no platform backend is started and no shared
// workspace state is touched — same precedent as the other src/mockups/* shots.
//
// Usage: node scripts/git-state-pill-shots.mjs <outDir> [port]
import { chromium } from '@playwright/test';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { existsSync, mkdirSync } from 'node:fs';
import { join, extname, resolve } from 'node:path';

const outDir = process.argv[2];
const port = Number(process.argv[3] || 4024);
if (!outDir) {
  console.error('usage: node scripts/git-state-pill-shots.mjs <outDir> [port]');
  process.exit(2);
}
mkdirSync(outDir, { recursive: true });

const candidates = [resolve('dist/git-state-pill-mockup/browser'), resolve('dist/git-state-pill-mockup')];
const distDir = candidates.find((d) => existsSync(join(d, 'index.html')));
if (!distDir) {
  console.error('missing build output: dist/git-state-pill-mockup[/browser]/index.html — run "ng build git-state-pill-mockup" first');
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
const page = await browser.newPage({ viewport: { width: 1280, height: 1000 }, deviceScaleFactor: 2 });
await page.goto(base, { waitUntil: 'networkidle' });

await page.waitForSelector('[data-testid="git-state-gallery"]');
// One AFTER pill per scenario must have rendered from the real view-model.
await page.waitForSelector('[data-testid^="after-"] [data-testid="task-card-git-state"]');
await page.waitForTimeout(300);

const shot = join(outDir, 'ASS-1665-git-state-badges--mocked.png');
await page.screenshot({ path: shot, fullPage: true });

// Log the rendered AFTER labels so the evidence is self-describing.
const scenarios = await page.locator('section.gallery__row').all();
for (const row of scenarios) {
  const id = await row.getAttribute('data-scenario');
  const after = await row.locator('[data-testid^="after-"] [data-testid="task-card-git-state"]').innerText().catch(() => '(no pill)');
  const kind = await row.locator('[data-testid^="after-"] [data-testid="task-card-git-state"]').getAttribute('class').catch(() => '');
  console.log(`scenario ${id} -> AFTER pill "${after.replace(/\s+/g, ' ').trim()}" [${kind}]`);
}

await browser.close();
await new Promise((r) => server.close(r));
console.log(`screenshot written: ${shot}`);
