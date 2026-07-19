/**
 * Case taxonomy for the Result view (Protocol -> Result redesign).
 *
 * A run's summary is no longer a single "one size fits all" bullet list.
 * Every run falls into one of a small set of *cases*, and each case gets a
 * template treatment tuned to what the reviewer needs to see first:
 *
 *   - **bugfix**     A defect was fixed. Lead with symptom -> root cause -> fix.
 *   - **feature**    New capability. Lead with what the user can now do.
 *   - **refactor**   Structure changed, behaviour held. Lead with the shape change.
 *   - **docs**       Documentation / concept work. Lead with what was written.
 *   - **forensics**  Investigation / diagnosis. Lead with the finding.
 *   - **ui-cleanup** Visual / UX polish. Lead with the before -> after.
 *   - **blocked**    The run did not fully land (blocked / partial / needs input).
 *                    Lead honestly with what stopped it and what remains.
 *   - **generic**    Fallback when nothing classifies with confidence.
 *
 * The classifier is deliberately deterministic and pure so it can run on the
 * client with zero backend round-trips and be unit-tested branch by branch.
 * It layers four evidence sources, strongest first:
 *
 *   1. explicit  a `- Case: <x>` hint the summary prompt emitted
 *   2. metadata  the task's structural type / mode / outcome verdict
 *   3. heuristic keyword signals scanned from the summary body
 *   4. fallback  `generic`
 *
 * The `blocked` framing is intentionally allowed to win over the work-type
 * guess: a blocked bugfix reads better as a "here is what stopped me" template
 * than as a triumphant "fix shipped" one. The underlying work-type is still
 * recoverable from the task metadata for anyone who needs it.
 */

export type ResultCase =
  | 'bugfix'
  | 'feature'
  | 'refactor'
  | 'docs'
  | 'forensics'
  | 'ui-cleanup'
  | 'blocked'
  | 'generic';

/** Where the winning classification came from (drives a tooltip + tests). */
export type ResultCaseConfidence = 'explicit' | 'metadata' | 'heuristic' | 'fallback';

export interface ResultCaseResult {
  case: ResultCase;
  confidence: ResultCaseConfidence;
  /** Short human-readable justification for the pick. */
  reason: string;
}

export interface ResultCaseInputs {
  /** `- Case: <x>` hint emitted by the summary prompt, if present. */
  hint?: string | null;
  /** Structural task type: `bug` | `feature` | `chore` (legacy values tolerated). */
  taskType?: string | null;
  /** Execution mode: `coding` | `research` | `planning` | ... */
  mode?: string | null;
  /** Outcome verdict kind from {@link deriveProtocolVerdict}. */
  verdictKind?: 'ok' | 'problem' | 'unclear' | null;
  /** Outcome verdict label, e.g. `Blocked`, `Partial`, `Needs input`, `Accepted`. */
  verdictLabel?: string | null;
  /** Summary body (status.md minus the header) used for keyword heuristics. */
  body?: string | null;
}

/**
 * How the overview's two lines are arranged. This is the per-case *template
 * divergence* (concept doc §8.3): beyond tuned labels + tone, each case picks a
 * visibly distinct layout so a bugfix, a UI cleanup, and a blocked run do not
 * all read as the same stacked pair.
 *
 *   - `standard`     stacked problem then solution (feature / docs / generic).
 *   - `sequence`     a stepped "A leads to B" flow with a connector arrow
 *                    (bugfix symptom -> fix, forensics investigated -> finding).
 *   - `before-after` two side-by-side columns with a "->" between them
 *                    (ui-cleanup before | after, refactor shape change).
 *   - `blocker`      the solution line is a prominent warn callout that leads
 *                    with where the run stopped (blocked / partial).
 */
export type ResultCaseLayout = 'standard' | 'sequence' | 'before-after' | 'blocker';

/** Presentation metadata for a case: how the Result head labels + tones it. */
export interface ResultCaseMeta {
  /** Human label shown on the case badge. */
  label: string;
  /** Leading glyph for the badge. */
  glyph: string;
  /** Semantic tone bucket -> CSS accent (`accent` | `warn` | `info` | `neutral`). */
  tone: 'accent' | 'warn' | 'info' | 'neutral';
  /** One-line intent shown under the overview when the body is thin. */
  blurb: string;
  /** Labels for the overview's two lines, tuned per case. */
  problemLabel: string;
  solutionLabel: string;
  /** Visibly distinct overview arrangement for this case (concept doc §8.3). */
  layout: ResultCaseLayout;
}

export const RESULT_CASE_META: Record<ResultCase, ResultCaseMeta> = {
  bugfix: {
    label: 'Bugfix',
    glyph: '🐞',
    tone: 'accent',
    blurb: 'A defect was diagnosed and fixed.',
    problemLabel: 'Symptom',
    solutionLabel: 'Fix',
    layout: 'sequence',
  },
  feature: {
    label: 'Feature',
    glyph: '✨',
    tone: 'accent',
    blurb: 'A new capability was added.',
    problemLabel: 'Goal',
    solutionLabel: 'What shipped',
    layout: 'standard',
  },
  refactor: {
    label: 'Refactor',
    glyph: '♻️',
    tone: 'info',
    blurb: 'Structure changed while behaviour held.',
    problemLabel: 'Motivation',
    solutionLabel: 'Change',
    layout: 'before-after',
  },
  docs: {
    label: 'Docs / Concept',
    glyph: '📝',
    tone: 'info',
    blurb: 'Documentation or a concept was written.',
    problemLabel: 'Question',
    solutionLabel: 'Written',
    layout: 'standard',
  },
  forensics: {
    label: 'Forensics',
    glyph: '🔬',
    tone: 'info',
    blurb: 'An investigation produced a finding.',
    problemLabel: 'Investigated',
    solutionLabel: 'Finding',
    layout: 'sequence',
  },
  'ui-cleanup': {
    label: 'UI Cleanup',
    glyph: '🎨',
    tone: 'accent',
    blurb: 'A visual or UX rough edge was polished.',
    problemLabel: 'Before',
    solutionLabel: 'After',
    layout: 'before-after',
  },
  blocked: {
    label: 'Blocked / Partial',
    glyph: '🚧',
    tone: 'warn',
    blurb: 'The run did not fully land. Read what stopped it.',
    problemLabel: 'Goal',
    solutionLabel: 'Where it stopped',
    layout: 'blocker',
  },
  generic: {
    label: 'Result',
    glyph: '📦',
    tone: 'neutral',
    blurb: 'A run completed.',
    problemLabel: 'Problem',
    solutionLabel: 'Solution',
    layout: 'standard',
  },
};

/** The set of cases a `- Case:` hint / metadata may resolve to (excludes derived `blocked`/`generic`). */
const WORK_CASES: readonly ResultCase[] = [
  'bugfix',
  'feature',
  'refactor',
  'docs',
  'forensics',
  'ui-cleanup',
];

/**
 * Normalise a free-text case hint to a known {@link ResultCase}. Accepts the
 * canonical ids plus a handful of natural synonyms the prompt might emit
 * (`bug` -> bugfix, `documentation` -> docs, `investigation` -> forensics,
 * `ui` / `cleanup` -> ui-cleanup). Returns null when nothing matches so the
 * caller can fall through to the metadata layer.
 */
export function normalizeCaseHint(hint: string | null | undefined): ResultCase | null {
  const t = (hint ?? '').trim().toLowerCase().replace(/[\s_]+/g, '-');
  if (!t) return null;
  if (WORK_CASES.includes(t as ResultCase)) return t as ResultCase;
  switch (t) {
    case 'bug':
    case 'fix':
    case 'bug-fix':
    case 'hotfix':
      return 'bugfix';
    case 'feat':
    case 'enhancement':
      return 'feature';
    case 'refactoring':
    case 'cleanup-code':
    case 'restructure':
      return 'refactor';
    case 'doc':
    case 'documentation':
    case 'concept':
    case 'design-doc':
    case 'planning':
      return 'docs';
    case 'investigation':
    case 'diagnosis':
    case 'diagnostic':
    case 'analysis':
    case 'research':
      return 'forensics';
    case 'ui':
    case 'cleanup':
    case 'ux':
    case 'styling':
    case 'polish':
      return 'ui-cleanup';
    case 'blocked':
    case 'partial':
      return 'blocked';
    default:
      return null;
  }
}

/** Keyword signals per case, scanned against the lower-cased summary body. */
const CASE_KEYWORDS: { case: ResultCase; words: readonly string[] }[] = [
  {
    case: 'ui-cleanup',
    words: ['spacing', 'padding', 'margin', 'alignment', 'stylesheet', 'css', 'scss', 'layout', 'responsive', 'contrast', 'readable', 'unreadable', 'screenshot', 'visual'],
  },
  {
    case: 'forensics',
    words: ['root cause', 'investigat', 'diagnos', 'reproduce', 'traced', 'why ', 'no code change', 'read-only', 'analysis only'],
  },
  {
    case: 'docs',
    words: ['documentation', 'readme', 'adr', 'design doc', 'concept', 'wrote docs', 'wiki', 'spec ', 'contract'],
  },
  {
    case: 'refactor',
    words: ['refactor', 'extracted', 'renamed', 'deduplicat', 'moved ', 'split ', 'consolidat', 'no behaviour change', 'no behavior change'],
  },
  {
    case: 'bugfix',
    words: ['fixed', 'bug', 'regression', 'broken', 'crash', 'defect', 'incorrect', 'off-by-one', 'null ', 'exception'],
  },
  {
    case: 'feature',
    words: ['added', 'new endpoint', 'implement', 'introduce', 'now supports', 'new component', 'feature'],
  },
];

/** Task label tokens that indicate an unsuccessful / incomplete run. */
function isBlockedVerdict(kind: string | null | undefined, label: string | null | undefined): boolean {
  if (kind === 'problem') return true;
  const l = (label ?? '').trim().toLowerCase();
  return l === 'partial' || l === 'needs input' || l === 'blocked' || l === 'failed';
}

/**
 * Classify a run into a {@link ResultCase}. Pure and deterministic. The
 * `blocked` framing wins whenever the outcome verdict is a non-success problem
 * / partial / needs-input, because that changes what the reader needs first.
 * Otherwise the strongest available work-type signal wins: explicit hint,
 * then metadata (taskType / mode), then body keywords, then `generic`.
 */
export function classifyResultCase(input: ResultCaseInputs): ResultCaseResult {
  // 1. Outcome framing first: a run that did not land reads as `blocked`,
  //    regardless of the underlying work type.
  if (isBlockedVerdict(input.verdictKind, input.verdictLabel)) {
    const label = (input.verdictLabel ?? 'blocked').trim() || 'blocked';
    return {
      case: 'blocked',
      confidence: 'metadata',
      reason: `Outcome verdict "${label}" -> lead with the blocker and what remains.`,
    };
  }

  // 2. Explicit prompt hint.
  const hinted = normalizeCaseHint(input.hint);
  if (hinted && hinted !== 'blocked') {
    return { case: hinted, confidence: 'explicit', reason: `Summary prompt tagged the run as "${hinted}".` };
  }

  // 3. Metadata: structural task type and execution mode.
  const type = (input.taskType ?? '').trim().toLowerCase();
  const mode = (input.mode ?? '').trim().toLowerCase();
  if (mode === 'research') {
    return { case: 'forensics', confidence: 'metadata', reason: 'Research-mode task -> investigation template.' };
  }
  if (mode === 'planning') {
    return { case: 'docs', confidence: 'metadata', reason: 'Planning-mode task -> docs / concept template.' };
  }
  if (type === 'bug') {
    return { case: 'bugfix', confidence: 'metadata', reason: 'Task type is "bug".' };
  }
  if (type === 'feature' || type === 'user-story') {
    return { case: 'feature', confidence: 'metadata', reason: 'Task type is "feature".' };
  }

  // 4. Keyword heuristics over the summary body. First matching case (in the
  //    priority order of CASE_KEYWORDS) wins.
  const body = (input.body ?? '').toLowerCase();
  if (body.trim()) {
    for (const entry of CASE_KEYWORDS) {
      const hit = entry.words.find((w) => body.includes(w));
      if (hit) {
        return { case: entry.case, confidence: 'heuristic', reason: `Summary body mentions "${hit.trim()}".` };
      }
    }
  }

  // 5. Fallback.
  return { case: 'generic', confidence: 'fallback', reason: 'No strong case signal; using the generic template.' };
}
