import { describe, expect, it } from 'vitest';
import {
  RESULT_CASE_META,
  classifyResultCase,
  normalizeCaseHint,
  type ResultCase,
  type ResultCaseInputs,
} from './result-case';

function classify(overrides: Partial<ResultCaseInputs>): ReturnType<typeof classifyResultCase> {
  return classifyResultCase({ verdictKind: 'ok', verdictLabel: 'Success', ...overrides });
}

describe('normalizeCaseHint', () => {
  it('accepts canonical ids verbatim', () => {
    expect(normalizeCaseHint('bugfix')).toBe('bugfix');
    expect(normalizeCaseHint('ui-cleanup')).toBe('ui-cleanup');
  });

  it('maps synonyms and tolerates spacing/case', () => {
    expect(normalizeCaseHint('Bug')).toBe('bugfix');
    expect(normalizeCaseHint('documentation')).toBe('docs');
    expect(normalizeCaseHint('  Investigation ')).toBe('forensics');
    expect(normalizeCaseHint('UI')).toBe('ui-cleanup');
    expect(normalizeCaseHint('feat')).toBe('feature');
  });

  it('returns null for unknown / empty hints', () => {
    expect(normalizeCaseHint('')).toBeNull();
    expect(normalizeCaseHint('   ')).toBeNull();
    expect(normalizeCaseHint('banana')).toBeNull();
  });
});

describe('classifyResultCase', () => {
  it('leads with blocked framing when the verdict is a problem, over any work type', () => {
    const r = classify({ verdictKind: 'problem', verdictLabel: 'Blocked', taskType: 'feature', hint: 'feature' });
    expect(r.case).toBe('blocked');
    expect(r.confidence).toBe('metadata');
  });

  it('treats Partial / Needs input as blocked framing', () => {
    expect(classify({ verdictKind: 'unclear', verdictLabel: 'Partial' }).case).toBe('blocked');
    expect(classify({ verdictKind: 'unclear', verdictLabel: 'Needs input' }).case).toBe('blocked');
  });

  it('does NOT treat a plain unclear (Running / Unclear) as blocked', () => {
    expect(classify({ verdictKind: 'unclear', verdictLabel: 'Running', taskType: 'bug' }).case).toBe('bugfix');
  });

  it('honours an explicit prompt hint above metadata', () => {
    const r = classify({ hint: 'refactor', taskType: 'bug' });
    expect(r.case).toBe('refactor');
    expect(r.confidence).toBe('explicit');
  });

  it('falls back to task metadata: bug -> bugfix, feature -> feature', () => {
    expect(classify({ taskType: 'bug' }).case).toBe('bugfix');
    expect(classify({ taskType: 'feature' }).case).toBe('feature');
    expect(classify({ taskType: 'user-story' }).case).toBe('feature');
  });

  it('maps research/planning modes to forensics/docs', () => {
    expect(classify({ mode: 'research' }).case).toBe('forensics');
    expect(classify({ mode: 'planning' }).case).toBe('docs');
  });

  it('uses body keywords when metadata is silent (chore task)', () => {
    expect(classify({ taskType: 'chore', body: 'Refactored the parser and extracted a helper.' }).case).toBe('refactor');
    expect(classify({ taskType: 'chore', body: 'Fixed a regression where the null check crashed.' }).case).toBe('bugfix');
    expect(classify({ taskType: 'chore', body: 'Adjusted padding and spacing on the card layout.' }).case).toBe('ui-cleanup');
    expect(classify({ taskType: 'chore', body: 'Investigated the root cause of the flaky run.' }).case).toBe('forensics');
  });

  it('falls back to generic when nothing classifies', () => {
    const r = classify({ taskType: 'chore', body: 'Did some stuff.' });
    expect(r.case).toBe('generic');
    expect(r.confidence).toBe('fallback');
  });

  it('every case has presentation metadata', () => {
    const cases: ResultCase[] = ['bugfix', 'feature', 'refactor', 'docs', 'forensics', 'ui-cleanup', 'blocked', 'generic'];
    for (const c of cases) {
      expect(RESULT_CASE_META[c]).toBeTruthy();
      expect(RESULT_CASE_META[c].label.length).toBeGreaterThan(0);
      expect(RESULT_CASE_META[c].problemLabel.length).toBeGreaterThan(0);
      expect(RESULT_CASE_META[c].solutionLabel.length).toBeGreaterThan(0);
    }
  });
});
