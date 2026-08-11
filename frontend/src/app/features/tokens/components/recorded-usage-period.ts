export interface TimestampedUsageModel {
  firstRecordedAt?: string | null;
  lastRecordedAt?: string | null;
}

export interface RecordedUsagePeriod {
  firstRecordedAt: string;
  lastRecordedAt: string;
}

const recordedDateFormatter = new Intl.DateTimeFormat('en', {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  timeZone: 'UTC',
});

const recordedDateTimeFormatter = new Intl.DateTimeFormat('en', {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
  timeZone: 'UTC',
});

/**
 * Derives the complete recording period for the supplied visible model rows.
 * A missing or malformed boundary makes the period unavailable instead of
 * presenting a partial range as if it covered every displayed token.
 */
export function recordedUsagePeriod(
  models: readonly TimestampedUsageModel[],
): RecordedUsagePeriod | null {
  if (models.length === 0) return null;

  let first = Number.POSITIVE_INFINITY;
  let last = Number.NEGATIVE_INFINITY;
  for (const model of models) {
    const modelFirst = Date.parse(model.firstRecordedAt ?? '');
    const modelLast = Date.parse(model.lastRecordedAt ?? '');
    if (!Number.isFinite(modelFirst) || !Number.isFinite(modelLast) || modelFirst > modelLast) {
      return null;
    }
    first = Math.min(first, modelFirst);
    last = Math.max(last, modelLast);
  }

  return {
    firstRecordedAt: new Date(first).toISOString(),
    lastRecordedAt: new Date(last).toISOString(),
  };
}

export function formatRecordedStart(timestamp: string): string {
  return recordedDateFormatter.format(new Date(timestamp));
}

export function formatRecordedAsOf(timestamp: string): string {
  return `${recordedDateTimeFormatter.format(new Date(timestamp))} UTC`;
}

export function recordedUsagePeriodLabel(period: RecordedUsagePeriod | null): string {
  if (!period) return 'Recording period unavailable';
  return `Since ${formatRecordedStart(period.firstRecordedAt)} · As of ${formatRecordedAsOf(period.lastRecordedAt)}`;
}
