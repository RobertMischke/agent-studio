import { describe, expect, it } from 'vitest';
import { formatUsageTelemetryPeriod } from './usage-time-range.util';

describe('formatUsageTelemetryPeriod', () => {
  it('uses the earliest and latest telemetry timestamps across sources', () => {
    expect(formatUsageTelemetryPeriod([
      { oldestRecordedAt: '2026-07-12T09:30:00Z', newestRecordedAt: '2026-08-10T08:00:00Z' },
      { oldestRecordedAt: '2026-07-11T18:00:00Z', newestRecordedAt: '2026-08-12T05:42:00Z' },
    ])).toBe('Since 11 Jul 2026 · as of 12 Aug 2026, 05:42 UTC');
  });

  it('does not substitute fetch time when telemetry bounds are absent', () => {
    expect(formatUsageTelemetryPeriod([{ oldestRecordedAt: null, newestRecordedAt: null }])).toBeNull();
  });
});
