// Disposable SPA static server for the mocked evidence screenshot.
// Serves the production build (dist/frontend/browser) with index.html
// fallback for client routes. Not committed product code; lives under
// results/ as a job artifact helper.
import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';

const ROOT = process.argv[2];
const PORT = Number(process.argv[3] || 4099);
const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.woff': 'font/woff',
  '.ttf': 'font/ttf', '.png': 'image/png', '.map': 'application/json',
  '.webmanifest': 'application/manifest+json',
};

const server = http.createServer(async (req, res) => {
  try {
    const urlPath = decodeURIComponent((req.url || '/').split('?')[0]);
    let filePath = join(ROOT, normalize(urlPath));
    let s = null;
    try { s = await stat(filePath); } catch { s = null; }
    if (!s || s.isDirectory()) filePath = join(ROOT, 'index.html');
    const data = await readFile(filePath);
    res.setHeader('content-type', MIME[extname(filePath)] || 'application/octet-stream');
    res.statusCode = 200;
    res.end(data);
  } catch (e) {
    res.statusCode = 500;
    res.end(String(e));
  }
});
server.listen(PORT, '127.0.0.1', () => console.log(`static server on http://127.0.0.1:${PORT} root=${ROOT}`));
