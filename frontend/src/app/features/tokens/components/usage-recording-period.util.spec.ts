import { describe, expect, it } from 'vitest';
import { usageRecordingPeriod } from './usage-recording-period.util';

describe('usageRecordingPeriod', () => {
  it('derives the oldest and newest bounds across contributing rows', () => {
    expect(usageRecordingPeriod([
      { firstRecordedAt: '2026-07-12T10:00:00Z', lastRecordedAt: '2026-08-11T09:05:00Z' },
      { firstRecordedAt: '2026-07-11T08:15:00Z', lastRecordedAt: '2026-08-10T14:42:00Z' },
    ])).toEqual({
      firstRecordedAt: '2026-07-11T08:15:00Z',
      lastRecordedAt: '2026-08-11T09:05:00Z',
      firstLabel: '11 Jul 2026',
      lastLabel: '11 Aug 2026, 09:05',
    });
  });

  it('does not substitute fetch or filesystem timestamps when entry bounds are absent', () => {
    expect(usageRecordingPeriod([{}])).toBeNull();
  });
});
