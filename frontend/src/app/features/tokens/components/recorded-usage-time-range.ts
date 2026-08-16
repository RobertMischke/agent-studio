export interface RecordedUsageTimeRange {
  firstRecordedAt: string;
  lastRecordedAt: string;
}

interface TimestampedUsage {
  firstRecordedAt?: string | null;
  lastRecordedAt?: string | null;
}

const dateFormatter = new Intl.DateTimeFormat('en-US', { dateStyle: 'medium' });
const timestampFormatter = new Intl.DateTimeFormat('en-US', {
  dateStyle: 'medium',
  timeStyle: 'short',
});

/** Fold the actual contributing telemetry bounds. Invalid and legacy rows are ignored. */
export function recordedUsageTimeRange(rows: readonly TimestampedUsage[]): RecordedUsageTimeRange | null {
  let first: { value: string; time: number } | null = null;
  let last: { value: string; time: number } | null = null;

  for (const row of rows) {
    const firstTime = parseTimestamp(row.firstRecordedAt);
    if (firstTime !== null && (first === null || firstTime < first.time)) {
      first = { value: row.firstRecordedAt!, time: firstTime };
    }
    const lastTime = parseTimestamp(row.lastRecordedAt);
    if (lastTime !== null && (last === null || lastTime > last.time)) {
      last = { value: row.lastRecordedAt!, time: lastTime };
    }
  }

  return first && last ? { firstRecordedAt: first.value, lastRecordedAt: last.value } : null;
}

export function formatRecordedUsageTimeRange(range: RecordedUsageTimeRange | null): string | null {
  if (!range) return null;
  return `Since ${dateFormatter.format(new Date(range.firstRecordedAt))} · As of ${timestampFormatter.format(new Date(range.lastRecordedAt))}`;
}

export function formatAsOf(value: string | null | undefined): string | null {
  return parseTimestamp(value) === null ? null : `As of ${timestampFormatter.format(new Date(value!))}`;
}

function parseTimestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}
