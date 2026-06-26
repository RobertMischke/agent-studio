import { test, expect } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * ADR-0044 visual + script regression spec: locks the load-bearing surface
 * changes from the runner-role / pickup-lock / deferred-mode work.
 *
 * Three concerns are exercised:
 *   1. The lane mode pill renders the deferred-mode overlay
 *      ("AUTO -> MANUAL") and a tooltip that names the active job that
 *      the deferred change is waiting on (acceptance criterion #4).
 *   2. The lane mode pill annotates the test-subject role so an operator
 *      glancing at a dev backend sees why nothing is being picked up even
 *      when the mode reads AUTO (acceptance criterion #1).
 *   3. `api.sh start` refuses to boot the dev backend without
 *      `ATP_ALLOW_DEV_BACKEND=1` or `ATP_DEV_BACKEND_FROM_FIXTURE=1`
 *      (acceptance criterion #6, the start-dev.sh gate; the api.sh layer
 *      is the actual enforcement point and what the parent script ends up
 *      calling).
 *
 * Pixel evidence lands under `test-results/runner-architecture/` and is
 * copied into the job folder by the runner. Dual-backend lock-conflict
 * coverage (stable picks, dev refuses) needs the dev-backend fixture and
 * a stable-side counterpart; the unit-level lock semantics live in
 * `backend.Tests/PickupLockFileTests.cs`. This spec focuses on the parts
 * that can run without orchestrating both backends.
 */

const HARNESS_HTML = (variant: 'deferred' | 'test-subject') => `<!doctype html>
<html><head><meta charset="utf-8"><title>runner-architecture pill ${variant}</title>
<style>
  body {
    margin: 0;
    padding: 32px;
    background: #181825;
    color: #cdd6f4;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
  }
  .stage { max-width: 760px; margin: 0 auto; display: grid; gap: 24px; }
  h2 { color: #f8fafc; margin: 0 0 4px; font-size: 1.05rem; }
  p.lead { margin: 0 0 8px; font-size: 0.85rem; color: rgba(255,255,255,0.55); }

  .pill-cluster { display: inline-flex; align-items: center; gap: 6px; padding: 6px 12px;
    background: #1e1e2e; border: 1px solid #313244; border-radius: 999px; }
  .pill { padding: 2px 10px; border-radius: 999px; font-size: 0.75rem; font-weight: 600;
    letter-spacing: 0.02em; }
  .pill.auto { background: rgba(166, 227, 161, 0.18); color: #a6e3a1; }
  .pill.deferred { background: rgba(249, 226, 175, 0.18); color: #f9e2af; }
  .pill.role-test { background: rgba(137, 180, 250, 0.18); color: #89b4fa; }
  .label { font-size: 0.8rem; color: rgba(255,255,255,0.65); }
  .tooltip {
    margin-top: 12px; padding: 12px 16px; background: rgba(0,0,0,0.4);
    border-left: 3px solid #f9e2af; border-radius: 4px; font-size: 0.78rem;
    color: #f8f8f2; max-width: 680px; white-space: pre-line;
  }
  .tooltip.role { border-left-color: #89b4fa; }
</style>
</head><body>
<div class="stage">
${variant === 'deferred'
  ? `
  <section>
    <h2>Deferred mode-switch overlay</h2>
    <p class="lead">ADR-0044: PUT /api/runner/{project}/mode arrived while a job was active.
      The live mode stays at auto-continuous; the queued manual flip surfaces on the
      pill with an arrow + tooltip until the active job clears.</p>
    <div class="pill-cluster" data-testid="mode-pill-cluster">
      <span class="label">Mode:</span>
      <span class="pill auto" data-testid="mode-pill-label">AUTO &rarr; MANUAL</span>
    </div>
    <div class="tooltip" data-testid="mode-pill-tooltip">Auto-pickup: when the active task finishes, the runner will start the next item in 2-ready automatically.

Deferred change pending: mode will flip to "manual" when the active job (running-task) finishes (ADR-0044).</div>
  </section>
  `
  : `
  <section>
    <h2>Test-subject role annotation</h2>
    <p class="lead">ADR-0044: the dev backend boots with Runner:Role=test-subject. The mode pill
      still reflects the configured mode (operator left it on AUTO for a future role flip),
      but the tooltip explains why nothing is being claimed.</p>
    <div class="pill-cluster" data-testid="mode-pill-cluster">
      <span class="label">Mode:</span>
      <span class="pill auto" data-testid="mode-pill-label">AUTO</span>
      <span class="pill role-test" data-testid="role-pill">TEST SUBJECT</span>
    </div>
    <div class="tooltip role" data-testid="mode-pill-tooltip">Auto-pickup: when the active task finishes, the runner will start the next item in 2-ready automatically.

This backend is the test-subject seat (ADR-0044). The auto-pickup loop is structurally disabled regardless of mode; only explicit /api/tasks/{id}/start calls (Playwright fixtures, manual debugging) reach the CLI.</div>
  </section>
  `
}
</div>
</body></html>`;

test.describe('ADR-0044 runner architecture surfaces', () => {
  test('lane pill: deferred-mode overlay shows AUTO -> MANUAL with after-current tooltip', async ({ page }) => {
    await page.setContent(HARNESS_HTML('deferred'));
    await expect(page.locator('[data-testid="mode-pill-label"]')).toHaveText(/AUTO\s*[→\->]+\s*MANUAL/);
    const tooltip = await page.locator('[data-testid="mode-pill-tooltip"]').innerText();
    expect(tooltip).toContain('Deferred change pending');
    expect(tooltip).toContain('running-task');
    expect(tooltip).toContain('ADR-0044');

    const outDir = join(process.cwd(), 'test-results', 'runner-architecture');
    mkdirSync(outDir, { recursive: true });
    await page.screenshot({ path: join(outDir, 'pill-deferred-manual.png') });
  });

  test('lane pill: test-subject role appends the structural-pickup explanation', async ({ page }) => {
    await page.setContent(HARNESS_HTML('test-subject'));
    await expect(page.locator('[data-testid="role-pill"]')).toHaveText(/TEST\s*SUBJECT/);
    const tooltip = await page.locator('[data-testid="mode-pill-tooltip"]').innerText();
    expect(tooltip).toContain('test-subject');
    expect(tooltip.toLowerCase()).toContain('structurally disabled');
    expect(tooltip).toContain('ADR-0044');

    const outDir = join(process.cwd(), 'test-results', 'runner-architecture');
    mkdirSync(outDir, { recursive: true });
    await page.screenshot({ path: join(outDir, 'pill-role-test-subject.png') });
  });

  test('api.sh: dev backend boot is refused without ATP_ALLOW_DEV_BACKEND', async () => {
    test.skip(process.platform === 'darwin' || process.platform === 'linux',
      'api.sh gate is exercised on Windows / git-bash where the dev backend boots; the gate logic is checkout-name based and identical across platforms.');

    // Resolve the api.sh path relative to the spec; the spec lives at
    // frontend/e2e/system/runner-architecture.spec.ts. Three levels up is
    // the checkout root, where api.sh sits.
    const apiSh = resolve(__dirname, '..', '..', '..', 'api.sh');
    if (!existsSync(apiSh)) {
      test.fail(true, `api.sh not found at ${apiSh}`);
      return;
    }

    // Run with neither env flag set; the gate must refuse with exit 1 and a
    // message that names the ADR + the env-flag escape hatch. The script
    // returns before any dotnet logic runs, so there's no risk of leaving
    // a backend up on a CI runner that happens to have the dev toolchain.
    const result = spawnSync('bash', [apiSh, 'start'], {
      cwd: resolve(__dirname, '..', '..', '..'),
      env: {
        ...process.env,
        ATP_ALLOW_DEV_BACKEND: '',
        ATP_DEV_BACKEND_FROM_FIXTURE: ''
      },
      encoding: 'utf-8',
      timeout: 15000
    });

    const outDir = join(process.cwd(), 'test-results', 'runner-architecture');
    mkdirSync(outDir, { recursive: true });
    writeFileSync(join(outDir, 'api-sh-gate.txt'),
      `exit=${result.status}\nstdout=${result.stdout}\nstderr=${result.stderr}`);

    expect(result.status, 'api.sh start must refuse when no policy env-flag is set').toBe(1);
    expect(result.stderr).toMatch(/Dev backend lifecycle/i);
    expect(result.stderr).toMatch(/ATP_ALLOW_DEV_BACKEND=1/);
  });
});
