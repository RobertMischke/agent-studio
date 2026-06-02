import type { CliExecution } from '../../../models/task.model';

export type TerminalRunOutcome =
  | 'success'
  | 'failed'
  | 'noop'
  | 'blocked'
  | 'needs-input'
  | 'interrupted'
  // A run that committed real work but exited non-zero without a terminal
  // sentinel (e.g. a watchdog-killed post-commit test run). Routed to review
  // as an honest partial - never shows the crash toast (handled below).
  | 'committed-partial'
  | 'unknown';

export function shouldShowFailureToast(execution: CliExecution | null): boolean {
  if (!execution) return false;
  if (execution.runOutcome) return execution.runOutcome === 'failed';
  return execution.status === 'failed';
}
