export interface TelemetryPeriodSource {
  firstActivity?: string | null;
  lastActivity?: string | null;
}

export interface TelemetryPeriod {
  firstActivity: string;
  lastActivity: string;
}

/**
 * Returns the exact recorded bounds only when every contributing aggregate
 * carries readable entry timestamps. This avoids presenting a partial range
 * as the lifetime of a larger total while an older cached payload is visible.
 */
export function deriveTelemetryPeriod(sources: readonly TelemetryPeriodSource[]): TelemetryPeriod | null {
  if (sources.length === 0) return null;
  let firstMs = Number.POSITIVE_INFINITY;
  let lastMs = Number.NEGATIVE_INFINITY;
  for (const source of sources) {
    const sourceFirst = Date.parse(source.firstActivity ?? '');
    const sourceLast = Date.parse(source.lastActivity ?? '');
    if (!Number.isFinite(sourceFirst) || !Number.isFinite(sourceLast)) return null;
    firstMs = Math.min(firstMs, sourceFirst);
    lastMs = Math.max(lastMs, sourceLast);
  }
  return {
    firstActivity: new Date(firstMs).toISOString(),
    lastActivity: new Date(lastMs).toISOString(),
  };
}

export function formatTelemetryPeriod(period: TelemetryPeriod): string {
  return `Recorded since ${formatUtcDate(period.firstActivity)} · as of ${formatUtcMinute(period.lastActivity)}`;
}

export function formatUtcDate(value: string): string {
  return new Date(value).toISOString().slice(0, 10);
}

export function formatUtcMinute(value: string): string {
  return new Date(value).toISOString().replace('T', ' ').slice(0, 16) + ' UTC';
}
