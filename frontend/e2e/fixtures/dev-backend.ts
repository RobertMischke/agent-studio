/**
 * Playwright fixture: dev backend lifecycle for specs running from stable.
 *
 * Why this exists: dev is the regression-test target. By convention dev's
 * backend is offline; only Playwright specs that explicitly need it may bring
 * it up. This fixture is the single way to do that. It calls
 * `scripts/supervisor/dev-lifecycle.sh` to start dev's backend on :5030 before
 * the spec runs and tears it down after, while staying idempotent: if the dev
 * backend was already healthy when the fixture loaded, the fixture leaves it
 * running on teardown.
 *
 * Set `KEEP_DEV_ON_FAIL=1` to keep dev up after a failing test for inspection.
 *
 * Resolution rules (no hard-coded paths):
 *   - DEV_CHECKOUT env var wins.
 *   - Else: ask the dev backend's `/api/watch-paths` endpoint after start
 *     (Agent Software Studio entry) for the workspace path.
 *   - Else: fall back to the script's own default (sibling folder).
 *
 * The fixture exposes:
 *   - port:      the dev backend port (number, default 5030).
 *   - baseUrl:   `http://127.0.0.1:<port>` for direct REST calls.
 *   - workspace: the dev checkout path (string), as reported by the backend.
 */
import { test as base, expect } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import * as path from 'node:path';

export interface DevBackend {
  port: number;
  baseUrl: string;
  workspace: string;
}

const DEV_PORT = Number(process.env.DEV_PORT ?? 5030);
const DEV_BASE_URL = `http://127.0.0.1:${DEV_PORT}`;

function resolveScriptPath(): string {
  // The fixture file lives at <repo>/frontend/e2e/fixtures/dev-backend.ts.
  // The script lives at <repo>/scripts/supervisor/dev-lifecycle.sh.
  // __dirname is not available in ESM; derive from import.meta-style cwd.
  const here = path.resolve(__dirname);
  const repoRoot = path.resolve(here, '..', '..', '..');
  return path.join(repoRoot, 'scripts', 'supervisor', 'dev-lifecycle.sh');
}

function runScript(cmd: 'start' | 'stop' | 'status'): { code: number; stdout: string; stderr: string } {
  const scriptPath = resolveScriptPath();
  if (!existsSync(scriptPath)) {
    throw new Error(`dev-lifecycle.sh not found at ${scriptPath}`);
  }
  // Use bash explicitly so this works on Windows Git Bash and Linux/macOS.
  const result = spawnSync('bash', [scriptPath, cmd], {
    env: {
      ...process.env,
      DEV_PORT: String(DEV_PORT),
    },
    encoding: 'utf8',
    timeout: 60_000,
  });
  return {
    code: result.status ?? 1,
    stdout: result.stdout ?? '',
    stderr: result.stderr ?? '',
  };
}

async function isHealthy(): Promise<boolean> {
  try {
    const res = await fetch(`${DEV_BASE_URL}/healthz`, { signal: AbortSignal.timeout(2000) });
    return res.ok;
  } catch {
    return false;
  }
}

async function discoverWorkspace(): Promise<string> {
  if (process.env.DEV_CHECKOUT) return process.env.DEV_CHECKOUT;
  try {
    const res = await fetch(`${DEV_BASE_URL}/api/watch-paths`, { signal: AbortSignal.timeout(5000) });
    if (res.ok) {
      const paths: Array<{ name?: string; rootPath?: string }> = await res.json();
      const ours = paths.find(p => (p.rootPath ?? '').toLowerCase().includes('agent-taskboard-dev'));
      if (ours?.rootPath) return ours.rootPath;
    }
  } catch {
    // fall through
  }
  // Last resort: same default the script uses.
  const here = path.resolve(__dirname);
  const repoRoot = path.resolve(here, '..', '..', '..');
  return path.resolve(repoRoot, '..', 'agent-taskboard-dev');
}

export const test = base.extend<{ devBackend: DevBackend }>({
  devBackend: async ({}, use, testInfo) => {
    const startedHealthy = await isHealthy();
    let weStartedIt = false;

    if (!startedHealthy) {
      const r = runScript('start');
      if (r.code !== 0) {
        throw new Error(
          `dev-lifecycle.sh start failed (exit ${r.code}).\nstdout:\n${r.stdout}\nstderr:\n${r.stderr}`
        );
      }
      weStartedIt = true;
      // Belt-and-braces: confirm before yielding.
      await expect.poll(() => isHealthy(), { timeout: 30_000, intervals: [500, 1000, 2000] }).toBe(true);
    }

    const workspace = await discoverWorkspace();

    await use({ port: DEV_PORT, baseUrl: DEV_BASE_URL, workspace });

    // Teardown: only stop what we started, and respect KEEP_DEV_ON_FAIL.
    if (!weStartedIt) return;
    const failed = testInfo.status !== testInfo.expectedStatus;
    if (failed && process.env.KEEP_DEV_ON_FAIL === '1') {
      console.log('[dev-backend fixture] test failed; KEEP_DEV_ON_FAIL=1 set, leaving dev backend running for inspection.');
      return;
    }
    const r = runScript('stop');
    if (r.code !== 0) {
      console.warn(`[dev-backend fixture] dev-lifecycle.sh stop returned ${r.code}\n${r.stderr}`);
    }
  },
});

export { expect };
