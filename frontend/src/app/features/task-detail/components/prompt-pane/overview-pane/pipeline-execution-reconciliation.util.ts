import type { PipelineStepExecution, PipelineStepStatus } from '../../../../task-pipeline';

/** Project a terminal CORE verdict over a legacy Pending plan row. */
export function reconcileCoreStatus(
  status: PipelineStepStatus | 'disabled',
  verdict: string | null,
): PipelineStepStatus | 'disabled' {
  if (status !== 'pending' && status !== 'planned') return status;
  const outcome = verdict?.trim().toLowerCase();
  if (!outcome) return status;
  return ['failed', 'failure', 'error', 'interrupted', 'committed-partial', 'cancelled', 'canceled', 'stopped'].includes(outcome)
    ? 'failed'
    : 'passed';
}

/** True when a persisted step contains evidence beyond its pre-filled plan row. */
export function hasRecordedStepExecution(step: PipelineStepExecution): boolean {
  return (step.status !== 'pending' && step.status !== 'planned')
    || step.startedAt != null
    || step.completedAt != null
    || step.durationMs > 0
    || step.inputTokens > 0
    || step.outputTokens > 0
    || step.cacheReadTokens > 0
    || step.cacheCreationTokens > 0
    || !!step.verdict?.trim()
    || !!step.reason?.trim();
}
