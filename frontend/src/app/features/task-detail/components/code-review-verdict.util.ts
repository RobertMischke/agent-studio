/**
 * Shared presentation helpers for the user-triggered code-review verdict.
 *
 * The raw verdict string comes from the code-review-step frontmatter
 * (backend `CodeReviewListEntry.verdict`): `pass` / `concerns` / `block`,
 * with any unrecognised value folded into `unknown`. Two surfaces classify
 * it and must stay in lockstep:
 *   - the Code Review tab list (`code-review-panel`), and
 *   - the compact commit-row rating badge in the Git pane's commit view
 *     (`git-pane`), the sensible successor to the token chip AGT-1990
 *     removed from that same line.
 *
 * Keeping the classification here means both read the same three known
 * tones + the same fallback, so a verdict never renders green in one place
 * and grey in the other.
 */
export type CodeReviewVerdictTone = 'pass' | 'concerns' | 'block' | 'unknown';

/** Fold a raw verdict string into one of the known tones. */
export function codeReviewVerdictTone(verdict: string | null | undefined): CodeReviewVerdictTone {
  const lower = (verdict ?? '').trim().toLowerCase();
  if (lower === 'pass') return 'pass';
  if (lower === 'concerns') return 'concerns';
  if (lower === 'block') return 'block';
  return 'unknown';
}

/** Single-glyph affordance for the compact commit-row badge. */
export function codeReviewVerdictGlyph(verdict: string | null | undefined): string {
  switch (codeReviewVerdictTone(verdict)) {
    case 'pass':
      return '✓';
    case 'concerns':
      return '!';
    case 'block':
      return '✕';
    default:
      return '·';
  }
}

/**
 * Short, title-cased label for the compact badge. Falls back to the raw
 * verdict (or a generic "Review") when the value isn't one of the known
 * tones, so an unexpected backend string still reads sensibly.
 */
export function codeReviewVerdictLabel(verdict: string | null | undefined): string {
  switch (codeReviewVerdictTone(verdict)) {
    case 'pass':
      return 'Pass';
    case 'concerns':
      return 'Concerns';
    case 'block':
      return 'Block';
    default: {
      const raw = (verdict ?? '').trim();
      return raw ? raw : 'Review';
    }
  }
}
