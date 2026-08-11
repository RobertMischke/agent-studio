import { describe, expect, it } from 'vitest';
import { deriveTelemetryPeriod, formatTelemetryPeriod } from './telemetry-period.util';

describe('telemetry period', () => {
  it('derives the oldest and newest entry timestamps across aggregates', () => {
    const period = deriveTelemetryPeriod([
      { firstActivity: '2026-07-11T08:15:00Z', lastActivity: '2026-08-10T17:04:00Z' },
      { firstActivity: '2026-07-09T10:00:00Z', lastActivity: '2026-08-11T19:42:18Z' },
    ]);

    expect(period).toEqual({
      firstActivity: '2026-07-09T10:00:00.000Z',
      lastActivity: '2026-08-11T19:42:18.000Z',
    });
    expect(formatTelemetryPeriod(period!))
      .toBe('Recorded since 2026-07-09 · as of 2026-08-11 19:42 UTC');
  });

  it('does not mislabel a partial timestamp set as the lifetime period', () => {
    expect(deriveTelemetryPeriod([
      { firstActivity: '2026-07-11T08:15:00Z', lastActivity: '2026-08-10T17:04:00Z' },
      { firstActivity: null, lastActivity: null },
    ])).toBeNull();
  });
});
