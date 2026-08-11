import { describe, expect, it } from 'vitest';
import { usageCoverage } from './usage-coverage.util';

describe('usageCoverage', () => {
  it('uses the oldest and newest telemetry events across the visible sources', () => {
    const coverage = usageCoverage([
      { firstRecordedAt: '2026-07-11T07:30:00Z', lastRecordedAt: '2026-08-10T18:00:00Z' },
      { firstRecordedAt: '2026-07-09T12:00:00Z', lastRecordedAt: '2026-08-11T16:42:00Z' },
    ]);

    expect(coverage?.firstRecordedAt).toBe('2026-07-09T12:00:00.000Z');
    expect(coverage?.lastRecordedAt).toBe('2026-08-11T16:42:00.000Z');
    expect(coverage?.label).toContain('Since 9 Jul 2026');
    expect(coverage?.label).toContain('as of 11 Aug 2026');
  });

  it('does not substitute fetch or cache timestamps when event bounds are absent', () => {
    expect(usageCoverage([{}])).toBeNull();
    expect(usageCoverage([{ firstRecordedAt: 'invalid', lastRecordedAt: 'invalid' }])).toBeNull();
  });
});
