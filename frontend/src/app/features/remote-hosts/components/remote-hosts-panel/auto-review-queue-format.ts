/**
 * Pure formatting helpers for the auto-review queue summary shown on the
 * Execution Hosts panel (AGT-2645). Kept out of the template so the numeric
 * shaping stays testable independent of rendering.
 */

/** "1.4/min", "-" when the rate is not a finite number. */
export function formatDrainRate(perMinute: number | null | undefined): string {
  if (perMinute === null || perMinute === undefined || !Number.isFinite(perMinute)) return '-';
  return `${perMinute.toFixed(1)}/min`;
}

/** "42s", "3m 10s", "3m" - or "-" when the duration is unknown. */
export function formatReviewDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) return '-';
  const totalSeconds = Math.round(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return seconds > 0 ? `${minutes}m ${seconds}s` : `${minutes}m`;
}
