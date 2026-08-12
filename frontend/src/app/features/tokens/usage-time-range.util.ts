export interface UsageTelemetryBounds {
  oldestRecordedAt?: string | null;
  newestRecordedAt?: string | null;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * Formats the actual telemetry coverage represented by one or more usage
 * aggregates. Fetch timestamps are deliberately excluded: the label answers
 * when the measured calls happened, not when the API response was created.
 */
export function formatUsageTelemetryPeriod(bounds: readonly UsageTelemetryBounds[]): string | null {
  let oldestMs: number | null = null;
  let newestMs: number | null = null;

  for (const value of bounds) {
    const oldest = parseTimestamp(value.oldestRecordedAt);
    const newest = parseTimestamp(value.newestRecordedAt);
    if (oldest !== null && (oldestMs === null || oldest < oldestMs)) oldestMs = oldest;
    if (newest !== null && (newestMs === null || newest > newestMs)) newestMs = newest;
  }

  if (oldestMs === null && newestMs === null) return null;
  if (oldestMs === null) return `As of ${formatUtcDateTime(newestMs!)}`;
  if (newestMs === null) return `Since ${formatUtcDate(oldestMs)}`;
  return `Since ${formatUtcDate(oldestMs)} · as of ${formatUtcDateTime(newestMs)}`;
}

function parseTimestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function formatUtcDate(value: number): string {
  const date = new Date(value);
  return `${pad(date.getUTCDate())} ${MONTHS[date.getUTCMonth()]} ${date.getUTCFullYear()}`;
}

function formatUtcDateTime(value: number): string {
  const date = new Date(value);
  return `${formatUtcDate(value)}, ${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())} UTC`;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}
