import {
  formatPhaseElapsed,
  lifecyclePhaseLabel,
  phaseStaticLabel,
  PHASE_LABELS,
} from './lifecycle-phase.util';

describe('formatPhaseElapsed', () => {
  it('formats sub-hour waits as m:ss', () => {
    expect(formatPhaseElapsed(0)).toBe('0:00');
    expect(formatPhaseElapsed(9_000)).toBe('0:09');
    expect(formatPhaseElapsed(75_000)).toBe('1:15');
    expect(formatPhaseElapsed(42_000)).toBe('0:42');
  });

  it('formats hour-plus waits as h:mm h - the 5-hour hang shape', () => {
    expect(formatPhaseElapsed(5 * 3600_000 + 7 * 60_000)).toBe('5:07h');
  });

  it('clamps clock skew to zero instead of rendering a negative wait', () => {
    expect(formatPhaseElapsed(-5_000)).toBe('0:00');
  });
});

describe('phaseStaticLabel', () => {
  it('returns null for no phase', () => {
    expect(phaseStaticLabel(null)).toBeNull();
    expect(phaseStaticLabel(undefined)).toBeNull();
    expect(phaseStaticLabel('')).toBeNull();
  });

  it('maps every backend lifecycle phase to a human label (no raw kebab leaks)', () => {
    for (const [phase, label] of Object.entries(PHASE_LABELS)) {
      const rendered = phaseStaticLabel(phase);
      expect(rendered).toBe(label);
      expect(rendered).not.toContain('-');
    }
  });

  it('falls back to the raw id only for an unknown future phase', () => {
    expect(phaseStaticLabel('some-new-phase')).toBe('some-new-phase');
  });
});

describe('lifecyclePhaseLabel', () => {
  const now = Date.parse('2026-07-11T00:05:00.000Z');

  it('renders loop-waiting with the elapsed timer from phaseEnteredAt', () => {
    const enteredAt = new Date(now - 42_000).toISOString();
    expect(lifecyclePhaseLabel('loop-waiting', enteredAt, null, now))
      .toBe('Waiting for loop continuation 0:42');
  });

  it('renders steer-pending from its durable marker, preferring steerPendingSince', () => {
    const enteredAt = new Date(now - 600_000).toISOString();
    const steerSince = new Date(now - 135_000).toISOString(); // 2m15s
    expect(lifecyclePhaseLabel('steer-pending', enteredAt, steerSince, now))
      .toBe('Waiting for answer 2:15');
  });

  it('falls back to phaseEnteredAt when steer-pending has no marker yet', () => {
    const enteredAt = new Date(now - 30_000).toISOString();
    expect(lifecyclePhaseLabel('steer-pending', enteredAt, null, now))
      .toBe('Waiting for answer 0:30');
  });

  it('shows the bare wait label when no start timestamp is parseable', () => {
    expect(lifecyclePhaseLabel('loop-waiting', null, null, now))
      .toBe('Waiting for loop continuation');
  });

  // Regression: the task-detail chip previously rendered these as raw kebab-case
  // ids ("intake-blocked") because its label map was incomplete. Sharing the map
  // with the board card closed that drift.
  it('renders non-timed phases as their static label, never a raw id', () => {
    expect(lifecyclePhaseLabel('intake-blocked', null, null, now)).toBe('Intake blocked');
    expect(lifecyclePhaseLabel('intake-passed', null, null, now)).toBe('Intake passed');
    expect(lifecyclePhaseLabel('post-processing-blocked', null, null, now)).toBe('Post processing blocked');
    expect(lifecyclePhaseLabel('awaiting-review', null, null, now)).toBe('Awaiting review');
    expect(lifecyclePhaseLabel('execution-running', null, null, now)).toBe('Execution running');
    expect(lifecyclePhaseLabel('human-ready', null, null, now)).toBe('Ready');
  });

  it('returns null when there is no phase', () => {
    expect(lifecyclePhaseLabel(null, null, null, now)).toBeNull();
  });
});
