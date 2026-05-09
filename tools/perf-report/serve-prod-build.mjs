#!/usr/bin/env node
// Tiny static + API-proxy server for the production-built frontend.
// Cycle 6 perf measurement: serves frontend/dist/frontend/browser at
// http://localhost:4012 and proxies /api/* + /hubs/* to the dev backend
// on http://localhost:5030. Lets Playwright run perf-baseline.spec.ts
// against an AOT/optimized build for the dev-vs-prod comparison the
// user asked for.
//
// Why not http-server / serve / express: zero new deps. Single-file
// Node http module with manual proxy + manual mime + index.html
// fallback. Good enough for one-shot perf measurement; not meant to be
// production hosting.

import { createServer, request as httpRequest } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { extname, join, normalize, resolve, sep } from 'node:path';

const PORT = parseInt(process.env.PORT || '4012', 10);
const BACKEND = process.env.BACKEND || 'http://localhost:5030';
const ROOT = resolve(process.cwd(), 'frontend/dist/frontend/browser');

if (!existsSync(ROOT)) {
  console.error(`Prod build not found at ${ROOT}. Run "npx ng build frontend --configuration production" first.`);
  process.exit(1);
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript',
  '.mjs': 'application/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.map': 'application/json',
};

function proxyToBackend(req, res) {
  const url = new URL(BACKEND);
  const opts = {
    hostname: url.hostname,
    port: parseInt(url.port || '80', 10),
    method: req.method,
    path: req.url,
    headers: { ...req.headers, host: `${url.hostname}:${url.port}` },
  };
  const upstream = httpRequest(opts, up => {
    res.writeHead(up.statusCode || 502, up.headers);
    up.pipe(res);
  });
  upstream.on('error', err => {
    res.writeHead(502, { 'content-type': 'text/plain' });
    res.end(`backend proxy error: ${err.message}`);
  });
  req.pipe(upstream);
}

async function serveStatic(req, res) {
  // Strip query string; resolve to file under ROOT.
  let urlPath = (req.url || '/').split('?')[0];
  if (urlPath === '/' || urlPath === '') urlPath = '/index.html';
  // Defence against path traversal: normalise + ensure inside ROOT.
  const filePath = normalize(join(ROOT, decodeURIComponent(urlPath)));
  if (!filePath.startsWith(ROOT + sep) && filePath !== ROOT) {
    res.writeHead(403); res.end(); return;
  }
  let target = filePath;
  try {
    const s = await stat(target);
    if (s.isDirectory()) target = join(target, 'index.html');
  } catch {
    // SPA fallback: anything that isn't a real file falls back to index.html
    // so Angular's router handles the route on the client.
    target = join(ROOT, 'index.html');
  }
  try {
    const body = await readFile(target);
    const ext = extname(target).toLowerCase();
    res.writeHead(200, {
      'content-type': MIME[ext] || 'application/octet-stream',
      'cache-control': 'no-cache' // measurement run; don't cache between runs
    });
    res.end(body);
  } catch (err) {
    res.writeHead(404, { 'content-type': 'text/plain' });
    res.end('not found');
  }
}

const server = createServer((req, res) => {
  const url = req.url || '';
  if (url.startsWith('/api/') || url.startsWith('/hubs/') || url === '/healthz') {
    proxyToBackend(req, res);
  } else {
    serveStatic(req, res).catch(err => {
      res.writeHead(500, { 'content-type': 'text/plain' });
      res.end(`internal: ${err.message}`);
    });
  }
});

server.listen(PORT, () => {
  console.log(`prod-build server: http://localhost:${PORT} (proxy /api -> ${BACKEND}, root ${ROOT})`);
});
