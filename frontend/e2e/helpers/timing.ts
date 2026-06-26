/**
 * Frontend timing helpers for Playwright specs.
 *
 * The backend perf gate ([backend.Tests/JobsEndpointPerfTests.cs]) catches
 * O(N^2) HTTP regressions. It does NOT catch the symptom the user actually
 * feels — UI lag while scrolling the detail panel, slow click-to-paint on
 * Create, anything that happens between a green API response and the DOM
 * settling. Those need measurement IN the browser.
 *
 * This module collects the three primitives we reach for repeatedly:
 *   - apiRoundtrip: how long the polled endpoint takes from inside the
 *     running app (matches what the user's Network panel would show, not
 *     what curl shows).
 *   - longTaskBudget: aggregate Long Tasks (> 50 ms blocking the main
 *     thread) over a window. This is the metric that tracks "the UI feels
 *     stuck" because Long Tasks are exactly what blocks scrolling, input,
 *     and animations.
 *   - clickToVisible: wall time between a click and the moment a target
 *     selector becomes attached + visible. Approximates "felt" latency
 *     for an action like opening the detail panel or creating a job.
 *
 * Each helper returns ms as a number so the caller does the assertion
 * with `expect(...).toBeLessThan(N)`. Keep ceilings generous to avoid
 * CI flakes; tighten only after a regression actually hits.
 */

import type { Page, Locator } from '@playwright/test';

/**
 * Times a specific outbound HTTP call from inside the running page. Use
 * this when you want the perf number that matches what the app's polling
 * actually pays — including Angular's HttpClient overhead, any
 * interceptors, and the browser's queue. The trigger callback is your
 * chance to provoke the request (click, navigation, programmatic refresh
 * via `evaluate`).
 *
 * Example:
 *   const ms = await apiRoundtrip(page, '**\/api/tasks/grouped', () =>
 *     page.evaluate(() => fetch('http://localhost:5030/api/tasks/grouped').then(r => r.json()))
 *   );
 *   expect(ms).toBeLessThan(1000);
 */
export async function apiRoundtrip(
  page: Page,
  urlGlob: string | RegExp,
  trigger: () => Promise<unknown>
): Promise<number> {
  const t0 = Date.now();
  const responsePromise = page.waitForResponse(urlGlob, { timeout: 30_000 });
  await trigger();
  const response = await responsePromise;
  // Use the response receipt timestamp rather than `await response.text()`
  // so we don't measure body parsing too (the symptom is wall-time first
  // byte / response, not deserialization).
  void response;
  return Date.now() - t0;
}

/**
 * Installs a PerformanceObserver in the page that aggregates Long Task
 * durations (browser-defined: any task that blocks the main thread for
 * > 50 ms) and returns a callback you invoke to read the running total.
 *
 * Use to assert "between mounting the detail panel and idle, the main
 * thread didn't stall for more than X ms total". This is the metric that
 * tracks scrolling smoothness; a small number of small Long Tasks is
 * fine, a few big ones (or many medium ones) is exactly what feels like
 * lag.
 */
export async function startLongTaskRecorder(page: Page): Promise<{
  totalMs: () => Promise<number>;
  count:   () => Promise<number>;
  stop:    () => Promise<void>;
}> {
  await page.evaluate(() => {
    const w = window as unknown as { __longTasks?: { total: number; count: number; observer?: PerformanceObserver } };
    if (w.__longTasks) return;
    const state = { total: 0, count: 0, observer: undefined as PerformanceObserver | undefined };
    try {
      const observer = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          state.total += entry.duration;
          state.count += 1;
        }
      });
      observer.observe({ type: 'longtask', buffered: true });
      state.observer = observer;
    } catch {
      // Long Task API unavailable on this platform; leave totals at 0
      // so the caller's assertion is permissive rather than failing
      // for the wrong reason.
    }
    w.__longTasks = state;
  });

  return {
    totalMs: () => page.evaluate(() => {
      const w = window as unknown as { __longTasks?: { total: number } };
      return w.__longTasks?.total ?? 0;
    }),
    count: () => page.evaluate(() => {
      const w = window as unknown as { __longTasks?: { count: number } };
      return w.__longTasks?.count ?? 0;
    }),
    stop: () => page.evaluate(() => {
      const w = window as unknown as { __longTasks?: { observer?: PerformanceObserver } };
      try { w.__longTasks?.observer?.disconnect(); } catch { /* ignore */ }
    })
  };
}

/**
 * Click a trigger and measure the wall time until the expected element
 * becomes visible. "Visible" is Playwright's standard visibility check
 * (attached + non-zero box). Use for action latency: "opening the
 * detail panel takes < 1500 ms", "create job click resolves a card in
 * the ready lane in < 2 s".
 */
export async function clickToVisible(
  trigger: Locator,
  target: Locator,
  timeoutMs = 10_000
): Promise<number> {
  const t0 = Date.now();
  await trigger.click();
  await target.waitFor({ state: 'visible', timeout: timeoutMs });
  return Date.now() - t0;
}
