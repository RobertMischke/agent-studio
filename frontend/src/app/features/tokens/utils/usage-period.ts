export interface UsageActivityBounds {
  firstActivity?: string | null;
  lastActivity?: string | null;
}

const dateFormatter = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC',
});

const timestampFormatter = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric', month: 'short', year: 'numeric',
  hour: '2-digit', minute: '2-digit', hour12: false,
  timeZone: 'UTC', timeZoneName: 'short',
});

/** Formats the oldest and newest telemetry timestamps folded into rows. */
export function formatUsagePeriod(rows: readonly UsageActivityBounds[]): string | null {
  const firstValues = parseActivities(rows.map(row => row.firstActivity));
  const lastValues = parseActivities(rows.map(row => row.lastActivity));
  if (firstValues.length === 0 || lastValues.length === 0) return null;
  const first = new Date(Math.min(...firstValues));
  const last = new Date(Math.max(...lastValues));
  return `Since ${dateFormatter.format(first)} · as of ${timestampFormatter.format(last)}`;
}

function parseActivities(values: readonly (string | null | undefined)[]): number[] {
  return values
    .map(value => value ? Date.parse(value) : NaN)
    .filter(value => Number.isFinite(value));
}
