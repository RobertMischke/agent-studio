import type { JobOutcomeIssue, JobSummaryStatus } from '../../../../models/job.model';

/**
 * Three-state simplified verdict shown at the very top of the protocol pane.
 * The user does not want shades of grey: every signal collapses to one of
 * good / bad / unclear so a glance answers the question "is this fine?".
 */
export type ProtocolVerdictKind = 'ok' | 'problem' | 'unclear';

export interface ProtocolVerdict {
  kind: ProtocolVerdictKind;
  emoji: string;
  label: string;
  detail: string;
}

export interface ProtocolVerdictInputs {
  isRunning: boolean;
  summaryStatus: JobSummaryStatus;
  /** Raw markdown of status.md (may be null/empty when no run yet). */
  statusMarkdown: string | null | undefined;
  outcomeIssue: JobOutcomeIssue | null | undefined;
  /** Has any cli-output / log activity been observed for this job at all? */
  hasActivity: boolean;
}

/**
 * Map the existing protocol-pane signals to a single 3-state verdict.
 *
 * Priority (top wins):
 *   1. isRunning            -> Unclear ("Läuft … Stand offen")
 *   2. summaryStatus failed -> Problem
 *   3. outcomeIssue High    -> Problem
 *   4. trailing sentinel    -> Done/NoOp = OK, Blocked = Problem, NeedsInput = Unclear
 *   5. Result: line         -> Success/NoOp = OK, Failed/Blocked = Problem, Partial/NeedsInput = Unclear
 *   6. outcomeIssue Warn    -> Unclear
 *   7. hasActivity          -> Unclear (ran but nothing classifiable yet)
 *   8. default              -> Unclear ("No run yet")
 *
 * Pure function so the protocol-pane component can wrap it in a `computed()`
 * and unit tests can hammer every branch without a fixture.
 */
export function deriveProtocolVerdict(input: ProtocolVerdictInputs): ProtocolVerdict {
  if (input.isRunning) {
    return verdict('unclear', 'Running', 'Agent is still working - click Interim status to peek.');
  }

  if (input.summaryStatus === 'failed') {
    return verdict('problem', 'Summary failed', 'Haiku could not summarise this run. See banner below.');
  }

  if (input.outcomeIssue?.severity?.toLowerCase() === 'high') {
    return verdict(
      'problem',
      input.outcomeIssue.label || 'Runner issue',
      input.outcomeIssue.summary || input.outcomeIssue.kind || 'Runner reported a high-severity issue.'
    );
  }

  const sentinel = parseSentinel(input.statusMarkdown);
  if (sentinel) {
    switch (sentinel.kind) {
      case 'done': return verdict('ok', 'Done', sentinel.reason || 'Agent reported the task as complete.');
      case 'noop': return verdict('ok', 'No action needed', sentinel.reason || 'Agent decided no work was required.');
      case 'blocked': return verdict('problem', 'Blocked', sentinel.reason || 'Agent reported a hard blocker.');
      case 'needs_input': return verdict('unclear', 'Needs input', sentinel.reason || 'Agent is waiting for clarification.');
    }
  }

  const result = parseResultLine(input.statusMarkdown);
  if (result) {
    switch (result) {
      case 'success': return verdict('ok', 'Success', 'Last run completed successfully.');
      case 'noop': return verdict('ok', 'No action needed', 'Last run produced no changes.');
      case 'failed': return verdict('problem', 'Failed', 'Last run failed - see protocol for details.');
      case 'blocked': return verdict('problem', 'Blocked', 'Last run is blocked.');
      case 'partial': return verdict('unclear', 'Partial', 'Last run did some of the work but not all.');
      case 'needsinput': return verdict('unclear', 'Needs input', 'Last run is waiting for clarification.');
    }
  }

  if (input.outcomeIssue?.severity?.toLowerCase() === 'warn') {
    return verdict(
      'unclear',
      input.outcomeIssue.label || 'Runner warning',
      input.outcomeIssue.summary || input.outcomeIssue.kind || 'Runner attached a warning to this task.'
    );
  }

  if (input.hasActivity) {
    return verdict('unclear', 'Unclear', 'The last run produced output but no clear verdict.');
  }

  return verdict('unclear', 'No run yet', 'Start the task to see how it goes.');
}

function verdict(kind: ProtocolVerdictKind, label: string, detail: string): ProtocolVerdict {
  return { kind, emoji: emojiFor(kind), label, detail };
}

function emojiFor(kind: ProtocolVerdictKind): string {
  switch (kind) {
    case 'ok':      return '🟢';
    case 'problem': return '🔴';
    case 'unclear': return '🟡';
  }
}

type SentinelKind = 'done' | 'blocked' | 'needs_input' | 'noop';
const SENTINEL_RE = /\[\[TASK_(DONE|BLOCKED|NEEDS_INPUT|NOOP)(?::([^\]]*))?\]\]/gi;

function parseSentinel(markdown: string | null | undefined): { kind: SentinelKind; reason: string | null } | null {
  if (!markdown) return null;
  let last: { kind: SentinelKind; reason: string | null } | null = null;
  SENTINEL_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = SENTINEL_RE.exec(markdown)) !== null) {
    last = { kind: m[1].toLowerCase() as SentinelKind, reason: (m[2] ?? '').trim() || null };
  }
  return last;
}

type ResultKind = 'success' | 'noop' | 'failed' | 'blocked' | 'partial' | 'needsinput';
const RESULT_RE = /^\s*-\s*Result:\s*([A-Za-z][A-Za-z _-]*)/im;

function parseResultLine(markdown: string | null | undefined): ResultKind | null {
  if (!markdown) return null;
  const m = RESULT_RE.exec(markdown);
  if (!m) return null;
  const normalised = m[1].trim().toLowerCase().replace(/[ _-]+/g, '');
  switch (normalised) {
    case 'success':    return 'success';
    case 'noop':       return 'noop';
    case 'failed':
    case 'fail':       return 'failed';
    case 'blocked':    return 'blocked';
    case 'partial':    return 'partial';
    case 'needsinput': return 'needsinput';
    default:           return null;
  }
}
