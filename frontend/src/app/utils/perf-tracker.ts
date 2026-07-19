/**
 * Lightweight perf-tracker for the caching-pass spans.
 *
 * Gated by either `?perf=1` in the URL OR `localStorage.perf === '1'`.
 * When neither is set, every call short-circuits — no performance.mark,
 * no console.time, no signal cost. The instrumentation is intentionally
 * pull-only: leaving it shipped costs nothing on a normal session.
 *
 * Spans (per docs/quality/frontend/perf-baseline-2026-05-28.md and the task prompt):
 *
 * | id                           | start hook                | end hook                          |
 * |------------------------------|---------------------------|-----------------------------------|
 * | accept-to-next-task          | `markAcceptClick` (state) | `markNextTaskRendered` (state)    |
 * | job-select-to-rendered       | `mark('job-select')`      | `measure('job-select-to-rendered',...)` |
 * | run-files-fetch              | `mark('run-files-fetch')` | `measure('run-files-rendered',...)`     |
 * | run-diff-fetch               | `mark('run-diff-fetch')`  | `measure('run-diff-rendered',...)`      |
 * | beautiful-results-render     | `mark('markdown-render')` | `measure('markdown-rendered',...)`      |
 *
 * The first one already existed; this module backstops the other four.
 *
 * Why a module function instead of an Angular service: signals + change
 * detection are not in play here. The call sites are imperative ("the
 * click just happened", "the panel just painted"); a service would only
 * add an `inject` boilerplate without adding any reactive value.
 */

let cachedEnabled: boolean | null = null;

function readFlag(): boolean {
  if (typeof window === 'undefined') return false;
  try {
    const params = new URLSearchParams(window.location.search);
    if (params.get('perf') === '1') return true;
  } catch { /* malformed URL — fall through */ }
  try {
    if (window.localStorage?.getItem('perf') === '1') return true;
  } catch { /* localStorage may be unavailable */ }
  return false;
}

/**
 * Returns true when perf tracking should fire. Cached on first read.
 * Call `resetPerfEnabledCache()` from a test to flip the gate between
 * cases.
 */
export function perfEnabled(): boolean {
  if (cachedEnabled === null) cachedEnabled = readFlag();
  return cachedEnabled;
}

export function resetPerfEnabledCache(): void {
  cachedEnabled = null;
}

/**
 * Drop a `performance.mark`. No-op when the flag is off OR the
 * Performance API is missing. Errors are swallowed so a saturated mark
 * buffer never breaks the call site.
 */
export function perfMark(name: string): void {
  if (!perfEnabled()) return;
  try {
    performance.mark(name);
  } catch { /* mark buffer full or API missing */ }
}

/**
 * Drop a `performance.measure` between two marks. No-op when the flag
 * is off, the API is missing, or either mark hasn't been recorded yet.
 * Also writes a one-line `console.timeLog`-shaped entry so the panel-
 * captured Long Task spec can correlate spans without opening DevTools.
 */
export function perfMeasure(measureName: string, startMark: string, endMark: string): void {
  if (!perfEnabled()) return;
  try {
    performance.measure(measureName, startMark, endMark);
    const entries = performance.getEntriesByName(measureName, 'measure');
    const last = entries[entries.length - 1];
    if (last && typeof console !== 'undefined') {
      console.info(`[perf] ${measureName}: ${last.duration.toFixed(1)} ms`);
    }
  } catch { /* one mark missing — drop the measure */ }
}
