import type { JobOutcomeIssue } from '../../../../../models/job.model';
import type { ClaudeRateLimitSnapshot, ClaudeSessionInfo } from '../../../../../features/claude';
import {
  formatTokens as fmtTokens,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn,
} from '../../../../../services/format.util';

export function formatTokens(n: number): string {
  return fmtTokens(n);
}

export function formatRateWindow(window: string | null): string {
  return fmtRateWindow(window);
}

export function formatResetIn(epoch: number, now: number): string {
  return fmtResetIn(epoch, now);
}

export function outcomeIssueExplanation(issue: JobOutcomeIssue): string {
  switch (issue.kind) {
    case 'permission-blocked':
      return 'The orchestrator detected a permission failure. It gets one soft intervention that asks the agent to continue with the permissions already available. If the same category appears again, the task is routed to Human Review.';
    case 'watchdog-timeout':
      return 'The watchdog stopped a run after it stopped producing progress. The task is surfaced for human review with the concrete timeout category instead of a generic heuristic fallback.';
    case 'missing-terminal-sentinel':
      return 'The agent replied with useful completion text but did not emit a terminal sentinel. The orchestrator asks once for a proper sentinel and then accepts or stops with this visible category.';
    case 'classifier-unknown':
      return 'The runner could not map the reply to a known completion shape. This is tracked separately so repeated classifier misses are visible at task and project level.';
    case 'heuristic-done':
      return 'The runner accepted a completed-looking reply through the compatibility heuristic. The category remains visible so these cases can be reduced over time.';
    default:
      return 'The runner attached a categorized outcome issue to this task. The raw source is still logs/cli-output.log.';
  }
}

export function formatIssueTime(iso: string | null | undefined): string {
  if (!iso) return 'unknown';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

export function claudeSessionTooltip(session: ClaudeSessionInfo | null): string {
  if (!session) return '';
  return [
    `Model: ${session.model ?? '?'}`,
    `Input: ${session.inputTokens.toLocaleString()} tokens`,
    `Output: ${session.outputTokens.toLocaleString()} tokens`,
    `Cache read: ${session.cacheReadTokens.toLocaleString()} tokens`,
    `Cache creation: ${session.cacheCreationTokens.toLocaleString()} tokens`,
    `Turns recorded: ${session.turnCount}`,
    session.lastTurnAt ? `Last turn: ${session.lastTurnAt}` : '',
  ]
    .filter(Boolean)
    .join('\n');
}

export function rateLimitTooltip(rateLimit: ClaudeRateLimitSnapshot | null, now: number): string {
  void now;
  if (!rateLimit) return '';
  const reset = rateLimit.resetsAt ? new Date(rateLimit.resetsAt * 1000).toLocaleString() : 'unknown';
  return [
    `Window: ${formatRateWindow(rateLimit.window)}`,
    `Status: ${rateLimit.status ?? '?'}`,
    `Resets at: ${reset}`,
    `Overage: ${rateLimit.overageStatus ?? '-'}`,
    rateLimit.isUsingOverage ? 'Currently using overage budget' : '',
    `Captured: ${new Date(rateLimit.capturedAt).toLocaleTimeString()}`,
  ]
    .filter(Boolean)
    .join('\n');
}
