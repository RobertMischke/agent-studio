export interface RecordedUsageRangeEntry {
  firstRecordedAt?: string | null;
  lastRecordedAt?: string | null;
}

export interface RecordedUsageRange {
  firstRecordedAt: Date;
  lastRecordedAt: Date;
  label: string;
}

const START_FORMAT = new Intl.DateTimeFormat('en', {
  timeZone: 'UTC',
  year: 'numeric',
  month: 'short',
  day: 'numeric',
});

const END_FORMAT = new Intl.DateTimeFormat('en', {
  timeZone: 'UTC',
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  timeZoneName: 'short',
});

/**
 * Derive the visible telemetry period from the entries themselves. Invalid or
 * legacy timestamps are ignored so a cache written before the range fields
 * existed never masquerades as a real collection boundary.
 */
export function recordedUsageRange(
  entries: readonly RecordedUsageRangeEntry[],
): RecordedUsageRange | null {
  const starts = entries
    .map(entry => parseTimestamp(entry.firstRecordedAt))
    .filter((value): value is number => value !== null);
  const ends = entries
    .map(entry => parseTimestamp(entry.lastRecordedAt))
    .filter((value): value is number => value !== null);
  if (starts.length === 0 || ends.length === 0) return null;

  const firstRecordedAt = new Date(Math.min(...starts));
  const lastRecordedAt = new Date(Math.max(...ends));
  return {
    firstRecordedAt,
    lastRecordedAt,
    label: `Since ${START_FORMAT.format(firstRecordedAt)} · As of ${END_FORMAT.format(lastRecordedAt)}`,
  };
}

function parseTimestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}
