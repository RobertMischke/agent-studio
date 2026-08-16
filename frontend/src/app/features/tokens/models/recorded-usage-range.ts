export interface RecordedUsageBounds {
  firstRecordedAt?: string | null;
  lastRecordedAt?: string | null;
}

export interface RecordedUsageRange {
  firstRecordedAt: string;
  lastRecordedAt: string;
}

/**
 * Derive a complete telemetry range from the entries that contribute to a
 * displayed aggregate. A partially dated legacy cache returns null instead of
 * presenting a narrower range as if it covered the full total.
 */
export function recordedUsageRange(rows: readonly RecordedUsageBounds[]): RecordedUsageRange | null {
  if (rows.length === 0) return null;

  let firstMs = Number.POSITIVE_INFINITY;
  let lastMs = Number.NEGATIVE_INFINITY;
  let firstRecordedAt = '';
  let lastRecordedAt = '';

  for (const row of rows) {
    const rowFirstMs = Date.parse(row.firstRecordedAt ?? '');
    const rowLastMs = Date.parse(row.lastRecordedAt ?? '');
    if (!Number.isFinite(rowFirstMs) || !Number.isFinite(rowLastMs)) return null;
    if (rowFirstMs < firstMs) {
      firstMs = rowFirstMs;
      firstRecordedAt = row.firstRecordedAt!;
    }
    if (rowLastMs > lastMs) {
      lastMs = rowLastMs;
      lastRecordedAt = row.lastRecordedAt!;
    }
  }

  return { firstRecordedAt, lastRecordedAt };
}

export function formatRecordedUsageStart(value: string): string {
  return new Date(value).toISOString().slice(0, 10);
}

export function formatRecordedUsageAsOf(value: string): string {
  return new Date(value).toISOString().replace('T', ' ').slice(0, 16) + 'Z';
}
