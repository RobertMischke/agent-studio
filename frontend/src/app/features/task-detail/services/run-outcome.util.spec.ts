import { describe, expect, it } from 'vitest';
import { shouldShowFailureToast } from './run-outcome.util';
import type { CliExecution } from '../../../models/task.model';

const baseExecution: CliExecution = {
  jobId: 'job',
  taskKey: 'watch::job',
  processId: 123,
  startedAt: '2026-05-12T00:00:00Z',
  status: 'failed',
  exitCode: -1,
  durationSeconds: 4,
  model: null,
  runOutcome: null
};

describe('shouldShowFailureToast', () => {
  it('suppresses the crash toast when the backend classified the run as noop', () => {
    expect(shouldShowFailureToast({ ...baseExecution, runOutcome: 'noop' })).toBe(false);
  });

  it('still shows the crash toast for canonical failed runs', () => {
    expect(shouldShowFailureToast({ ...baseExecution, runOutcome: 'failed' })).toBe(true);
  });

  it('suppresses the crash toast for a committed-partial run (committed work, downstream step killed)', () => {
    expect(shouldShowFailureToast({ ...baseExecution, runOutcome: 'committed-partial' })).toBe(false);
  });

  it('keeps legacy failed execution behavior when no runOutcome is present', () => {
    expect(shouldShowFailureToast({ ...baseExecution, runOutcome: null })).toBe(true);
  });
});
