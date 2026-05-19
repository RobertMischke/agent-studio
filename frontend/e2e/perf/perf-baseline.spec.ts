import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { apiRoundtrip, clickToVisible, startLongTaskRecorder } from '../helpers/timing';

/**
 * Baseline frontend perf measurement run. Captures repeatable numbers per
 * UI surface and writes them to logs/perf/frontend-<scenario>-latest.json
 * so the HTML report generator in tools/perf-report can fold them into the
 * before/after comparison.
 *
 * This is a measurement spec, not a regression gate. The hard gates live
 * in perf-frontend.spec.ts; tightening them after each cycle is a separate
 * concern.
 *
 * Gated by env var RUN_PERF_BASELINE=1 so it does not slow the default
 * Playwright run. Scenario tag picks up from PERF_SCENARIO env var, default
 * "baseline".
 *
 * Run with:
 *   RUN_PERF_BASELINE=1 PERF_SCENARIO=baseline \
 *     npx playwright test e2e/perf-baseline.spec.ts --project=chromium
 */

const PROJECT_NAME = 'Agent Software Studio';
const ITERATIONS = parseInt(process.env.PERF_ITERATIONS || '10', 10);

interface FrontendMetric {
  surface: string;
  metric: string;
  unit: 'ms' | 'count' | 'bytes';
  iterations: number;
  samples: number[];
  notes?: string;
}

// Playwright re-evaluates the spec module per test in some configurations,
// so module-scope arrays do not accumulate. Append each measurement to a
// JSONL file under logs/perf/ and consolidate at run end via the generator.
const SCENARIO = process.env.PERF_SCENARIO || 'baseline';
const REPO_ROOT = path.resolve(process.cwd(), '..');
const PERF_DIR = path.join(REPO_ROOT, 'logs', 'perf');
const RUN_TAG = process.env.PERF_RUN_TAG || new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
const JSONL_PATH = path.join(PERF_DIR, `frontend-${SCENARIO}-${RUN_TAG}.jsonl`);

function appendMetric(m: FrontendMetric) {
  fs.mkdirSync(PERF_DIR, { recursive: true });
  fs.appendFileSync(JSONL_PATH, JSON.stringify(m) + '\n');
}

function quantile(sorted: number[], q: number): number {
  if (sorted.length === 1) return sorted[0];
  const pos = q * (sorted.length - 1);
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  if (lo === hi) return sorted[lo];
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}

function statsLine(samples: number[]): string {
  const sorted = [...samples].sort((a, b) => a - b);
  const p50 = quantile(sorted, 0.5);
  const p95 = quantile(sorted, 0.95);
  const max = sorted[sorted.length - 1];
  return `p50=${p50.toFixed(1)}ms p95=${p95.toFixed(1)}ms max=${max.toFixed(1)}ms (n=${samples.length})`;
}

function record(surface: string, metric: string, unit: FrontendMetric['unit'], samples: number[], notes?: string) {
  appendMetric({ surface, metric, unit, iterations: samples.length, samples, notes });
}

test.describe('Frontend perf baseline', () => {
  test.beforeAll(() => {
    if (process.env.RUN_PERF_BASELINE !== '1') {
      test.skip(true, 'Set RUN_PERF_BASELINE=1 to capture baseline.');
    }
  });

  // Each test appends to ${JSONL_PATH}. The HTML generator consolidates
  // the JSONL into the latest JSON. We avoid afterAll because Playwright
  // re-evaluates the module per test in some setups, which loses any
  // module-scope accumulator.

  test('board: grouped-jobs roundtrip from browser', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);
    const samples: number[] = [];
    for (let i = 0; i < ITERATIONS; i++) {
      const ms = await apiRoundtrip(
        page,
        /\/api\/jobs\/grouped(\?|$)/,
        () => page.evaluate(() => fetch('/api/jobs/grouped').then(r => r.text()))
      );
      samples.push(ms);
    }
    record('board', '/api/jobs/grouped roundtrip', 'ms', samples);
    console.log(`board grouped roundtrip: ${statsLine(samples)}`);
  });

  test('board: runner/status roundtrip from browser', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);
    const samples: number[] = [];
    for (let i = 0; i < ITERATIONS; i++) {
      const ms = await apiRoundtrip(
        page,
        /\/api\/runner\/status(\?|$)/,
        () => page.evaluate(() => fetch('/api/runner/status').then(r => r.text()))
      );
      samples.push(ms);
    }
    record('board', '/api/runner/status roundtrip', 'ms', samples);
    console.log(`board runner/status roundtrip: ${statsLine(samples)}`);
  });

  test('board: 10s idle long-task budget + network requests', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    let requestCount = 0;
    let bytesIn = 0;
    page.on('request', () => requestCount++);
    page.on('response', async r => {
      try {
        const buf = await r.body().catch(() => null);
        if (buf) bytesIn += buf.byteLength;
      } catch { /* ignore */ }
    });

    const recorder = await startLongTaskRecorder(page);
    await page.waitForTimeout(10_000);
    const total = await recorder.totalMs();
    const count = await recorder.count();
    await recorder.stop();

    record('board', 'longTask total over 10s idle', 'ms', [total],
      `${count} long tasks observed`);
    record('board', 'network requests over 10s idle', 'count', [requestCount]);
    record('board', 'network bytes received over 10s idle', 'bytes', [bytesIn]);
    console.log(`board idle 10s: longTasks=${total.toFixed(0)}ms (${count} tasks), reqs=${requestCount}, bytes=${bytesIn}`);
  });

  test('project-detail: click-to-visible', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const samples: number[] = [];
    for (let i = 0; i < ITERATIONS; i++) {
      const trigger = page.getByTestId(`project-shell-open-${PROJECT_NAME}`);
      await expect(trigger).toBeVisible({ timeout: 10_000 });
      const target = page.getByTestId('project-detail');
      const ms = await clickToVisible(trigger, target, 10_000);
      samples.push(ms);
      // Close panel before next iteration; if there's no explicit close we
      // navigate away and back.
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(300);
    }
    record('project-detail', 'click-to-visible', 'ms', samples);
    console.log(`project-detail click-to-visible: ${statsLine(samples)}`);
  });

  test('project-detail: 10s idle long-task budget + network', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);
    const trigger = page.getByTestId(`project-shell-open-${PROJECT_NAME}`);
    await trigger.click();
    await page.getByTestId('project-detail').waitFor({ state: 'visible', timeout: 10_000 });
    await page.waitForTimeout(500);

    let requestCount = 0;
    let bytesIn = 0;
    page.on('request', () => requestCount++);
    page.on('response', async r => {
      try {
        const buf = await r.body().catch(() => null);
        if (buf) bytesIn += buf.byteLength;
      } catch { /* ignore */ }
    });

    const recorder = await startLongTaskRecorder(page);
    await page.waitForTimeout(10_000);
    const total = await recorder.totalMs();
    const count = await recorder.count();
    await recorder.stop();

    record('project-detail', 'longTask total over 10s idle', 'ms', [total],
      `${count} long tasks observed`);
    record('project-detail', 'network requests over 10s idle', 'count', [requestCount]);
    record('project-detail', 'network bytes received over 10s idle', 'bytes', [bytesIn]);
    console.log(`project-detail idle 10s: longTasks=${total.toFixed(0)}ms (${count} tasks), reqs=${requestCount}, bytes=${bytesIn}`);
  });

  test('task-detail: click-to-visible', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const samples: number[] = [];
    for (let i = 0; i < Math.min(ITERATIONS, 5); i++) {
      // Find any job card and click it.
      const cards = page.getByTestId('job-card');
      const count = await cards.count();
      if (count === 0) {
        test.skip(true, 'No job cards visible to measure task-detail open');
        return;
      }
      const trigger = cards.nth(i % count);
      await trigger.scrollIntoViewIfNeeded();
      const target = page.getByTestId('detail-panes');
      const ms = await clickToVisible(trigger, target, 10_000);
      samples.push(ms);
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(500);
    }
    if (samples.length > 0) {
      record('task-detail', 'click-to-visible', 'ms', samples);
      console.log(`task-detail click-to-visible: ${statsLine(samples)}`);
    }
  });

  test('task-detail: 10s idle long-task budget + network', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const cards = page.getByTestId('job-card');
    const cardCount = await cards.count();
    if (cardCount === 0) {
      test.skip(true, 'No job cards visible to measure task-detail');
      return;
    }
    await cards.first().click();
    const target = page.getByTestId('detail-panes');
    await target.waitFor({ state: 'visible', timeout: 10_000 });
    await page.waitForTimeout(500);

    let requestCount = 0;
    let bytesIn = 0;
    page.on('request', () => requestCount++);
    page.on('response', async r => {
      try {
        const buf = await r.body().catch(() => null);
        if (buf) bytesIn += buf.byteLength;
      } catch { /* ignore */ }
    });

    const recorder = await startLongTaskRecorder(page);
    await page.waitForTimeout(10_000);
    const total = await recorder.totalMs();
    const count = await recorder.count();
    await recorder.stop();

    record('task-detail', 'longTask total over 10s idle', 'ms', [total],
      `${count} long tasks observed`);
    record('task-detail', 'network requests over 10s idle', 'count', [requestCount]);
    record('task-detail', 'network bytes received over 10s idle', 'bytes', [bytesIn]);
    console.log(`task-detail idle 10s: longTasks=${total.toFixed(0)}ms (${count} tasks), reqs=${requestCount}, bytes=${bytesIn}`);
  });
});
