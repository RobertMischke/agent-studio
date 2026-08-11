export interface UsageCoverageSource {
  firstRecordedAt?: string | null;
  lastRecordedAt?: string | null;
}

export interface UsageCoverage {
  firstRecordedAt: string;
  lastRecordedAt: string;
  label: string;
}

const DATE_FORMAT = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
});

const DATE_TIME_FORMAT = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
  timeZoneName: 'short',
});

/**
 * Builds the displayed telemetry range from event timestamps only. Fetch,
 * cache, and file modification timestamps are intentionally excluded because
 * they describe transport freshness, not the period represented by totals.
 */
export function usageCoverage(sources: readonly UsageCoverageSource[]): UsageCoverage | null {
  const starts = validInstants(sources.map(source => source.firstRecordedAt));
  const ends = validInstants(sources.map(source => source.lastRecordedAt));
  if (sources.length === 0 || starts.length !== sources.length || ends.length !== sources.length) return null;

  const first = new Date(Math.min(...starts));
  const last = new Date(Math.max(...ends));
  return {
    firstRecordedAt: first.toISOString(),
    lastRecordedAt: last.toISOString(),
    label: `Since ${DATE_FORMAT.format(first)} · as of ${DATE_TIME_FORMAT.format(last)}`,
  };
}

function validInstants(values: readonly (string | null | undefined)[]): number[] {
  return values
    .map(value => value ? Date.parse(value) : Number.NaN)
    .filter(value => Number.isFinite(value));
}
