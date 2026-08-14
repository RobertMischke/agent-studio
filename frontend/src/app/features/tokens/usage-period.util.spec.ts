import { recordedUsagePeriod } from './usage-period.util';

describe('recordedUsagePeriod', () => {
  it('uses the oldest and newest telemetry events rather than fetch time', () => {
    expect(recordedUsagePeriod([
      { firstRecordedAt: '2026-06-02T08:15:00Z', lastRecordedAt: '2026-07-12T09:00:00Z' },
      { firstRecordedAt: '2026-06-18T10:00:00Z', lastRecordedAt: '2026-08-11T15:45:00Z' },
    ])).toBe('Since 2 Jun 2026 · As of 11 Aug 2026, 15:45 UTC');
  });

  it('does not invent a range when either boundary is unavailable', () => {
    expect(recordedUsagePeriod([{ firstRecordedAt: '2026-06-02T08:15:00Z' }])).toBeNull();
    expect(recordedUsagePeriod([{ firstRecordedAt: 'invalid', lastRecordedAt: 'invalid' }])).toBeNull();
  });
});
