import type { JobOutcomeIssue, JobSummaryStatus } from '../../../../models/task.model';

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
 *   1. isRunning                        -> Unclear ("Läuft … Stand offen")
 *   2. summaryStatus failed             -> Problem
 *   3. outcomeIssue High                -> Problem
 *   4. trailing sentinel                -> Done/NoOp = OK, Blocked = Problem, NeedsInput = Unclear
 *   5. Result: Success + Notes/Open Items/What Was Done contains a blocker phrase
 *                                       -> Problem (downgraded; matched sentence becomes detail)
 *   6. Result: line                     -> Success/NoOp = OK, Failed/Blocked = Problem, Partial/NeedsInput = Unclear
 *   7. outcomeIssue Warn                -> Unclear
 *   8. hasActivity                      -> Unclear (ran but nothing classifiable yet)
 *   9. default                          -> Unclear ("No run yet")
 *
 * Step 5 exists because the `Result:` line is deterministically rewritten by the
 * runner from the terminal exit, not by Haiku, so it can read `Success` while the
 * body Notes describe a blocker the agent hit (sandbox denied, access denied,
 * external verification required, etc.). The downgrade is intentionally narrow
 * to `Result: Success`; sentinels and explicit non-success Result lines still
 * win at their original priority.
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
    if (result === 'success') {
      const blocker = scanForBlockers(input.statusMarkdown);
      if (blocker) {
        const detail = blocker.sentence
          ? `${blocker.section}: ${blocker.sentence}`
          : `${blocker.section} flagged a blocker (matched "${blocker.phrase}").`;
        return verdict('problem', 'Blocked', detail);
      }
    }
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

// Phrases that flip a Haiku "Success" verdict into a Blocked verdict when they
// appear in the Notes / Open Items / What Was Done body of status.md. Keep one
// phrase per line so the list reads as a lint surface. EN + DE because the agent
// log can be either.
const BLOCKER_PHRASES = [
  'blocked',
  'blocker',
  'could not',
  'was prevented',
  'prevented from',
  'sandbox denied',
  'access denied',
  'forbidden',
  'requires manual',
  'requires external',
  'geblockt',
  'konnte nicht',
  'verhindert'
] as const;

const BLOCKER_SECTION_HEADINGS = new Set(['notes', 'open items', 'what was done']);

interface BlockerHit {
  phrase: string;
  /** The full sentence (or list-bullet line) containing the phrase, trimmed. */
  sentence: string;
  /** Human-readable section heading (e.g. "Notes"). */
  section: string;
}

export function scanForBlockers(markdown: string | null | undefined): BlockerHit | null {
  if (!markdown) return null;
  for (const section of splitSections(markdown)) {
    if (!BLOCKER_SECTION_HEADINGS.has(section.heading.trim().toLowerCase())) continue;
    const hit = findBlockerPhrase(section.body);
    if (hit) return { ...hit, section: section.heading.trim() };
  }
  return null;
}

function splitSections(markdown: string): { heading: string; body: string }[] {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const out: { heading: string; body: string }[] = [];
  let current: { heading: string; body: string[] } | null = null;
  for (const line of lines) {
    const m = /^##\s+(.*)$/.exec(line);
    if (m) {
      if (current) out.push({ heading: current.heading, body: current.body.join('\n') });
      current = { heading: m[1], body: [] };
    } else if (current) {
      current.body.push(line);
    }
  }
  if (current) out.push({ heading: current.heading, body: current.body.join('\n') });
  return out;
}

function findBlockerPhrase(body: string): { phrase: string; sentence: string } | null {
  const lower = body.toLowerCase();
  let best: { phrase: string; index: number } | null = null;
  for (const phrase of BLOCKER_PHRASES) {
    const idx = lower.indexOf(phrase);
    if (idx >= 0 && (best === null || idx < best.index)) {
      best = { phrase, index: idx };
    }
  }
  if (!best) return null;
  return { phrase: best.phrase, sentence: extractSentence(body, best.index) };
}

function extractSentence(text: string, atIndex: number): string {
  let start = atIndex;
  while (start > 0) {
    const ch = text[start - 1];
    if (ch === '\n' || ch === '.' || ch === '!' || ch === '?') break;
    start--;
  }
  let end = atIndex;
  while (end < text.length) {
    const ch = text[end];
    if (ch === '\n') break;
    if (ch === '.' || ch === '!' || ch === '?') { end++; break; }
    end++;
  }
  return text.slice(start, end).trim().replace(/^[-*\s]+/, '').trim();
}
