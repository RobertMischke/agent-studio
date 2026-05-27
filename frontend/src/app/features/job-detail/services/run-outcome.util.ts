import type { CliExecution } from '../../../models/task.model';

export type TerminalRunOutcome =
  | 'success'
  | 'failed'
  | 'noop'
  | 'blocked'
  | 'needs-input'
  | 'interrupted'
  | 'unknown';

export function shouldShowFailureToast(execution: CliExecution | null): boolean {
  if (!execution) return false;
  if (execution.runOutcome) return execution.runOutcome === 'failed';
  return execution.status === 'failed';
}
