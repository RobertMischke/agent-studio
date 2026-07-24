import { describe, expect, it } from 'vitest';
import type { TaskOutcomeIssue } from '../../../../../models/task.model';
import { presentOutcomeFailure } from './outcome-failure-presentation';

function issue(kind: string, summary: string, technicalDetails?: string): TaskOutcomeIssue {
  return { kind, label: 'Internal label', severity: 'High', summary, technicalDetails, lastSeenAt: null };
}

describe('presentOutcomeFailure', () => {
  it('humanizes a watchdog timeout with its silence threshold', () => {
    const raw = '[orchestrator] [watchdog-timeout] auto-cancelled after 601s of silence. [phase=TurnCompleted silence=601s allowed=600s]';

    expect(presentOutcomeFailure(issue('watchdog-timeout', raw))).toEqual({
      primaryText: 'Run automatically stopped after 10 minutes without progress (watchdog).',
      rawDiagnostic: raw,
    });
  });

  it.each([
    ['tool-router-error', 'The tool call did not return a usable result.'],
    ['no-reply', 'The agent did not produce a response.'],
  ])('humanizes %s without exposing its raw tokens', (kind, primaryText) => {
    const raw = `[orchestrator] [${kind}] phase=ToolExecuting silence=42s`;

    expect(presentOutcomeFailure(issue(kind, raw))).toEqual({ primaryText, rawDiagnostic: raw });
  });

  it('uses a complete generic sentence for an unknown kind and preserves full technical details', () => {
    const summary = 'Compact text...';
    const technicalDetails = '[orchestrator] [future-kind] phase=TurnCompleted allowed=600s complete-tail';

    expect(presentOutcomeFailure(issue('future-kind', summary, technicalDetails))).toEqual({
      primaryText: 'The run failed for an unexpected reason.',
      rawDiagnostic: technicalDetails,
    });
  });
});
