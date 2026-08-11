import { describe, expect, it } from 'vitest';
import { recordedUsagePeriod, recordedUsagePeriodLabel } from './recorded-usage-period';

describe('recorded usage period', () => {
  it('takes the oldest and newest telemetry boundaries across visible models', () => {
    const period = recordedUsagePeriod([
      { firstRecordedAt: '2026-03-02T14:00:00Z', lastRecordedAt: '2026-08-10T18:05:00Z' },
      { firstRecordedAt: '2026-01-17T09:15:00Z', lastRecordedAt: '2026-08-11T12:42:00Z' },
    ]);

    expect(period).toEqual({
      firstRecordedAt: '2026-01-17T09:15:00.000Z',
      lastRecordedAt: '2026-08-11T12:42:00.000Z',
    });
    expect(recordedUsagePeriodLabel(period)).toContain('Since Jan 17, 2026');
    expect(recordedUsagePeriodLabel(period)).toContain('As of Aug 11, 2026, 12:42 UTC');
  });

  it('does not claim a complete period when any displayed model lacks boundaries', () => {
    expect(recordedUsagePeriod([
      { firstRecordedAt: '2026-01-17T09:15:00Z', lastRecordedAt: '2026-08-11T12:42:00Z' },
      { firstRecordedAt: null, lastRecordedAt: null },
    ])).toBeNull();
  });
});
