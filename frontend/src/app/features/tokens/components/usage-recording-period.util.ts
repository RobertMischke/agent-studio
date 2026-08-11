export interface TimestampedUsageRow {
  firstRecordedAt?: string | null;
  lastRecordedAt?: string | null;
}

export interface UsageRecordingPeriod {
  firstRecordedAt: string;
  lastRecordedAt: string;
  firstLabel: string;
  lastLabel: string;
}

const DAY_FORMATTER = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  timeZone: 'UTC',
});

const TIMESTAMP_FORMATTER = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
  timeZone: 'UTC',
});

/**
 * Derives the visible recording bounds from the telemetry rows themselves.
 * Fetch timestamps and file modification times are intentionally excluded.
 */
export function usageRecordingPeriod(rows: readonly TimestampedUsageRow[]): UsageRecordingPeriod | null {
  const timestamps = rows
    .flatMap(row => [row.firstRecordedAt, row.lastRecordedAt])
    .filter((value): value is string => typeof value === 'string' && value.length > 0)
    .map(value => ({ value, millis: Date.parse(value) }))
    .filter(({ millis }) => Number.isFinite(millis) && new Date(millis).getUTCFullYear() > 1)
    .sort((a, b) => a.millis - b.millis);

  const first = timestamps[0];
  const last = timestamps.at(-1);
  if (!first || !last) return null;

  return {
    firstRecordedAt: first.value,
    lastRecordedAt: last.value,
    firstLabel: DAY_FORMATTER.format(first.millis),
    lastLabel: TIMESTAMP_FORMATTER.format(last.millis),
  };
}
