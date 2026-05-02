import { describe, expect, it } from 'vitest';
import { classifyOutcome } from './agent-outcome.util';

describe('classifyOutcome', () => {
  it('returns unknown for empty input', () => {
    const r = classifyOutcome('');
    expect(r.kind).toBe('unknown');
    expect(r.suggestions).toHaveLength(0);
  });

  it('detects "done" when the agent reports a commit / fix', () => {
    const r = classifyOutcome([
      'I read the relevant files and applied the fix.',
      '',
      'Layout-Fix committed.',
      'Commit: 37c05c2 fix(protocol-pane): push telemetry chips to the right edge.'
    ].join('\n'));

    expect(r.kind).toBe('done');
    expect(r.suggestions.length).toBeGreaterThan(0);
    expect(r.suggestions.some((s) => /Looks good/i.test(s.label))).toBe(true);
  });

  it('detects "blocked" and offers a workaround chip', () => {
    const r = classifyOutcome([
      'Tried to read the secrets file.',
      "I don't have access to the credentials directory, so I cannot proceed."
    ].join('\n'));

    expect(r.kind).toBe('blocked');
    expect(r.suggestions.some((s) => /Try anyway|Skip/i.test(s.label))).toBe(true);
  });

  it('detects a trailing question and extracts it', () => {
    const r = classifyOutcome([
      "I see two possible approaches.",
      "Should I split the migration into two PRs?"
    ].join('\n'));

    expect(r.kind).toBe('question');
    expect(r.question).toBe('Should I split the migration into two PRs?');
    expect(r.summary.toLowerCase()).toContain('asking');
    // The "Yes, do it" chip should reflect the question content.
    expect(r.suggestions.some((s) => /Yes/i.test(s.label))).toBe(true);
  });

  it('treats "I\'ll wait for your request" as needs_input even without a question mark', () => {
    const r = classifyOutcome("I'll wait for your request.");
    expect(['needs_input', 'question']).toContain(r.kind);
    expect(r.suggestions.some((s) => /continue/i.test(s.prompt))).toBe(true);
  });

  it('falls back to progress chips for in-flight reports', () => {
    const r = classifyOutcome('Investigating the failing test now.');
    expect(r.kind).toBe('progress');
    expect(r.suggestions.some((s) => /Keep going/i.test(s.label))).toBe(true);
  });

  it('does not classify the entire reply, only the tail (avoids false-positive blocked)', () => {
    // "cannot find" appears mid-reply but the agent finishes with a commit;
    // the heuristic should land on "done", not "blocked".
    const r = classifyOutcome([
      'Earlier I cannot find the right path.',
      'After some searching I located it.',
      'Implemented the fix.',
      'Done — committed as a3b4c5d.'
    ].join('\n'));

    expect(r.kind).toBe('done');
  });
});
