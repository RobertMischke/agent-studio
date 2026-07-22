import type { TaskOutcomeIssue } from '../../../../../models/task.model';

export interface OutcomeFailurePresentation {
  primaryText: string;
  rawDiagnostic: string;
}

function watchdogDurationSeconds(raw: string): number | null {
  const match = /\ballowed=(\d+(?:\.\d+)?)s\b/i.exec(raw)
    ?? /\b(?:auto-cancelled|killed)\s+after\s+(\d+(?:\.\d+)?)s\s+of\s+silence\b/i.exec(raw)
    ?? /\bsilence=(\d+(?:\.\d+)?)s\b/i.exec(raw);
  if (!match) return null;
  const seconds = Number(match[1]);
  return Number.isFinite(seconds) && seconds > 0 ? seconds : null;
}

function humanDuration(seconds: number): string {
  if (seconds < 60) {
    const rounded = Math.max(1, Math.round(seconds));
    return `${rounded} second${rounded === 1 ? '' : 's'}`;
  }
  const rounded = Math.max(1, Math.round(seconds / 60));
  return `${rounded} minute${rounded === 1 ? '' : 's'}`;
}

export function presentOutcomeFailure(issue: TaskOutcomeIssue): OutcomeFailurePresentation {
  const rawDiagnostic = issue.technicalDetails || issue.summary || issue.label || issue.kind;
  const kind = issue.kind.trim().toLowerCase();

  switch (kind) {
    case 'watchdog-timeout': {
      const seconds = watchdogDurationSeconds(rawDiagnostic);
      return {
        primaryText: seconds == null
          ? 'Run automatically stopped after prolonged inactivity (watchdog).'
          : `Run automatically stopped after ${humanDuration(seconds)} without progress (watchdog).`,
        rawDiagnostic,
      };
    }
    case 'tool-router-error':
      return { primaryText: 'The tool call did not return a usable result.', rawDiagnostic };
    case 'no-reply':
      return { primaryText: 'The agent did not produce a response.', rawDiagnostic };
    default:
      return { primaryText: 'The run failed for an unexpected reason.', rawDiagnostic };
  }
}
