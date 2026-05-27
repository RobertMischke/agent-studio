import type { ClaudeRateLimitSnapshot, ClaudeSessionInfo } from '../../claude';
import type { CliType } from '../../../models/task.model';
import {
  cliTypeLabel as fmtCliTypeLabel,
  formatDate as fmtDate,
  formatDateTime as fmtDateTime,
  formatMultiplier as fmtMultiplier,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn,
  formatTime as fmtTime,
  formatTokens as fmtTokens,
  stateLabel as fmtStateLabel,
} from '../../../services/format.util';

export function formatTokens(n: number): string {
  return fmtTokens(n);
}

export function formatMultiplier(mult: number | null): string {
  return fmtMultiplier(mult);
}

export function cliTypeLabel(cliType: CliType): string {
  return fmtCliTypeLabel(cliType);
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

export function formatRateWindow(window: string | null): string {
  return fmtRateWindow(window);
}

export function formatResetIn(epochSeconds: number, now: number): string {
  return fmtResetIn(epochSeconds, now);
}

export function rateLimitTooltip(rateLimit: ClaudeRateLimitSnapshot | null): string {
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

export function stateLabel(state: string): string {
  return fmtStateLabel(state);
}

export function formatTime(dateStr: string): string {
  return fmtTime(dateStr);
}

export function formatDate(dateStr: string): string {
  return fmtDate(dateStr);
}

export function formatDateTime(dateStr: string): string {
  return fmtDateTime(dateStr);
}

export function isCliErrorMessage(message: string | null | undefined): boolean {
  return !!message && /cli|copilot|authenticat/i.test(message);
}
