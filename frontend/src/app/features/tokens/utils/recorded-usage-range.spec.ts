import { describe, expect, it } from 'vitest';
import { recordedUsageRange } from './recorded-usage-range';

describe('recordedUsageRange', () => {
  it('uses the oldest start and newest end from the visible model buckets', () => {
    const range = recordedUsageRange([
      { firstRecordedAt: '2026-08-03T10:00:00Z', lastRecordedAt: '2026-08-09T15:30:00Z' },
      { firstRecordedAt: '2026-08-01T08:15:00Z', lastRecordedAt: '2026-08-11T17:42:00Z' },
    ]);

    expect(range?.firstRecordedAt.toISOString()).toBe('2026-08-01T08:15:00.000Z');
    expect(range?.lastRecordedAt.toISOString()).toBe('2026-08-11T17:42:00.000Z');
    expect(range?.label).toBe('Since Aug 1, 2026 · As of Aug 11, 2026, 5:42 PM UTC');
  });

  it('does not invent dates for legacy buckets without telemetry timestamps', () => {
    expect(recordedUsageRange([{ firstRecordedAt: null, lastRecordedAt: undefined }])).toBeNull();
  });
});
