export interface RecordedUsageRangeSource {
  firstRecordedAt?: string | null;
  lastRecordedAt?: string | null;
}

/**
 * Formats the real event boundary carried by token telemetry. Fetch times are
 * deliberately excluded: refreshing an aggregate must not make its usage look
 * newer than the latest recorded model call.
 */
export function recordedUsagePeriod(sources: readonly RecordedUsageRangeSource[]): string | null {
  const first = boundary(sources.map(source => source.firstRecordedAt), Math.min);
  const last = boundary(sources.map(source => source.lastRecordedAt), Math.max);
  if (first === null || last === null) return null;

  const firstLabel = new Intl.DateTimeFormat('en-GB', {
    day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC',
  }).format(first);
  const lastLabel = new Intl.DateTimeFormat('en-GB', {
    day: 'numeric', month: 'short', year: 'numeric',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23', timeZone: 'UTC',
  }).format(last);
  return `Since ${firstLabel} · As of ${lastLabel} UTC`;
}

function boundary(
  values: readonly (string | null | undefined)[],
  select: (left: number, right: number) => number,
): number | null {
  let selected: number | null = null;
  for (const value of values) {
    if (!value) continue;
    const parsed = Date.parse(value);
    if (!Number.isFinite(parsed)) continue;
    selected = selected === null ? parsed : select(selected, parsed);
  }
  return selected;
}
