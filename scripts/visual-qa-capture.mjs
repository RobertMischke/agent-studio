#!/usr/bin/env node

import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import net from 'node:net';

function parseArgs(argv) {
  const options = { routes: [] };
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key?.startsWith('--') || value === undefined) throw new Error(`Invalid argument near ${key ?? '<end>'}`);
    if (key === '--route') options.routes.push(value);
    else options[key.slice(2).replaceAll('-', '_')] = value;
  }
  return options;
}

function safeName(value) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 60) || 'affected-view';
}

function reservePort() {
  return new Promise((resolvePort, reject) => {
    const server = net.createServer();
    server.unref();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => resolvePort(address.port));
    });
  });
}

async function waitForUrl(url, timeoutMs = 180_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(2_000) });
      if (response.ok) return;
    } catch { /* the build is still starting */ }
    await new Promise(resolveWait => setTimeout(resolveWait, 750));
  }
  throw new Error(`Angular app did not become ready within ${Math.round(timeoutMs / 1000)}s: ${url}`);
}

function stopTree(child) {
  if (!child || child.exitCode !== null) return;
  try {
    if (process.platform === 'win32') {
      spawn('taskkill', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore', windowsHide: true });
    } else {
      process.kill(-child.pid, 'SIGTERM');
    }
  } catch {
    try { child.kill('SIGTERM'); } catch { /* best effort */ }
  }
}

async function bootApp(repository, backendUrl) {
  await waitForUrl(`${backendUrl}/healthz`, 10_000);
  const port = await reservePort();
  const scratch = await mkdtemp(join(tmpdir(), 'agent-studio-visual-qa-'));
  const proxyPath = join(scratch, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({
    '/api': { target: backendUrl, secure: false, changeOrigin: true },
    '/hubs': { target: backendUrl, secure: false, changeOrigin: true, ws: true },
    '/healthz': { target: backendUrl, secure: false, changeOrigin: true },
  }, null, 2));
  const angularCli = join(repository, 'frontend', 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
  const child = spawn(process.execPath, [
    angularCli,
    'serve',
    'frontend',
    '--host', '127.0.0.1',
    '--port', String(port),
    '--configuration', 'production',
    '--proxy-config', proxyPath,
  ], {
    cwd: join(repository, 'frontend'),
    detached: process.platform !== 'win32',
    env: { ...process.env, CI: '1', NG_CLI_ANALYTICS: 'false' },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  let output = '';
  child.stdout.on('data', chunk => { output = `${output}${chunk}`.slice(-16_000); });
  child.stderr.on('data', chunk => { output = `${output}${chunk}`.slice(-16_000); });
  child.once('exit', code => {
    if (code && output) process.stderr.write(output);
  });
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await Promise.race([
      waitForUrl(baseUrl),
      new Promise((_, reject) => child.once('exit', code => reject(
        new Error(`Angular app exited before capture (code ${code ?? 'unknown'}).`)))),
    ]);
    return { baseUrl, child };
  } catch (error) {
    stopTree(child);
    throw new Error(`${error.message}\n${output}`);
  }
}

const options = parseArgs(process.argv.slice(2));
const repository = resolve(options.repository ?? process.cwd());
const output = resolve(options.output ?? process.cwd());
const manifestPath = resolve(options.manifest ?? join(output, 'capture.json'));
const captures = [];
const errors = [];
let browser;
let app;

await mkdir(output, { recursive: true });
try {
  if (!options.base_url && !options.backend_url) throw new Error('--backend-url is required when --base-url is absent');
  if (options.routes.length === 0) throw new Error('At least one --route label::path is required');
  app = options.base_url
    ? { baseUrl: options.base_url.replace(/\/$/, ''), child: null }
    : await bootApp(repository, options.backend_url.replace(/\/$/, ''));

  const requireFromFrontend = createRequire(join(repository, 'frontend', 'package.json'));
  const { chromium } = requireFromFrontend('playwright-core');
  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1000 },
    colorScheme: 'light',
    deviceScaleFactor: 1,
  });

  for (const routeValue of options.routes) {
    const delimiter = routeValue.indexOf('::');
    const label = delimiter > 0 ? routeValue.slice(0, delimiter) : 'affected-view';
    const route = delimiter > 0 ? routeValue.slice(delimiter + 2) : routeValue;
    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.message));
    try {
      const url = new URL(route, `${app.baseUrl}/`).toString();
      await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45_000 });
      await page.locator('body').waitFor({ state: 'visible', timeout: 15_000 });
      let settled = true;
      try {
        await page.waitForFunction(
          () => document.querySelector('[data-testid^="loading-surface-"]') === null,
          undefined,
          { timeout: 15_000 });
      } catch {
        settled = false;
      }
      await page.waitForTimeout(750);
      const filename = `${String(captures.length + 1).padStart(2, '0')}-${safeName(label)}--real.png`;
      await page.screenshot({ path: join(output, filename), fullPage: true });
      captures.push({ label, route, file: filename, title: await page.title(), settled, pageErrors });
    } catch (error) {
      errors.push(`${label} (${route}): ${error.message}`);
    } finally {
      await page.close();
    }
  }
} catch (error) {
  errors.push(error.message);
} finally {
  try { await browser?.close(); } catch { /* best effort */ }
  stopTree(app?.child);
  const manifest = {
    schemaVersion: 1,
    ok: captures.length > 0 && errors.length === 0,
    baseUrl: app?.baseUrl ?? null,
    captures,
    errors,
    capturedAt: new Date().toISOString(),
  };
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2));
  process.stdout.write(`${JSON.stringify(manifest)}\n`);
  if (!manifest.ok) process.exitCode = 1;
}
