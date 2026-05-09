/**
 * Visibility-aware setInterval wrapper.
 *
 * Cycle 3 perf: every recurring poller in the app should skip its tick when
 * the document is hidden (other tab, minimised window, screen off). Pre-Cycle-3
 * only the live-board poll had this guard; the other 11 pollers (cli-output,
 * run-timeline, session-events, screenshots, claude-session, git-pane,
 * hygiene-strip, project-detail refreshAll, now-tick, auto-review-status,
 * git-summary, git-hygiene) all kept hitting the backend at full cadence
 * against a tab the user wasn't even looking at, paying CPU + network for
 * data nobody could see.
 *
 * The runtime cost of the guard itself is one branch per tick — irrelevant.
 * The benefit is roughly: open the app, switch to another tab for an hour →
 * zero non-essential HTTP traffic instead of thousands of polls.
 *
 * Usage:
 *   private timer = setVisibleInterval(() => this.refresh(), 5000);
 *   ngOnDestroy() { clearVisibleInterval(this.timer); }
 *
 * Returns the same handle shape as setInterval (so existing code that stores
 * `ReturnType<typeof setInterval>` keeps compiling). The wrapped callback
 * runs only when `document.hidden === false`.
 */
export type VisibleIntervalHandle = ReturnType<typeof setInterval>;

export function setVisibleInterval(
  callback: () => void,
  ms: number
): VisibleIntervalHandle {
  return setInterval(() => {
    if (typeof document !== 'undefined' && document.hidden) return;
    callback();
  }, ms);
}

export function clearVisibleInterval(handle: VisibleIntervalHandle | null): void {
  if (handle != null) clearInterval(handle);
}

/**
 * Like setVisibleInterval but the first tick is delayed by `ms` (matching
 * setInterval semantics). Provided for symmetry — callers that want
 * "fire now, then every ms" should call the callback themselves first.
 */
export function setVisibleIntervalDelayed(
  callback: () => void,
  ms: number
): VisibleIntervalHandle {
  return setVisibleInterval(callback, ms);
}
