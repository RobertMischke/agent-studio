import { describe, expect, it } from 'vitest';
import {
  codeReviewVerdictGlyph,
  codeReviewVerdictLabel,
  codeReviewVerdictTone,
} from './code-review-verdict.util';

describe('code-review-verdict.util', () => {
  it('folds known verdicts into tones case-insensitively', () => {
    expect(codeReviewVerdictTone('pass')).toBe('pass');
    expect(codeReviewVerdictTone('PASS')).toBe('pass');
    expect(codeReviewVerdictTone(' Concerns ')).toBe('concerns');
    expect(codeReviewVerdictTone('block')).toBe('block');
  });

  it('maps unknown / empty verdicts to the unknown tone', () => {
    expect(codeReviewVerdictTone('great')).toBe('unknown');
    expect(codeReviewVerdictTone('')).toBe('unknown');
    expect(codeReviewVerdictTone(null)).toBe('unknown');
    expect(codeReviewVerdictTone(undefined)).toBe('unknown');
  });

  it('labels known tones with a title-cased word', () => {
    expect(codeReviewVerdictLabel('pass')).toBe('Pass');
    expect(codeReviewVerdictLabel('concerns')).toBe('Concerns');
    expect(codeReviewVerdictLabel('block')).toBe('Block');
  });

  it('falls back to the raw verdict (or a generic label) for unknown values', () => {
    expect(codeReviewVerdictLabel('great')).toBe('great');
    expect(codeReviewVerdictLabel('')).toBe('Review');
    expect(codeReviewVerdictLabel(null)).toBe('Review');
  });

  it('gives each tone a distinct glyph', () => {
    const glyphs = new Set([
      codeReviewVerdictGlyph('pass'),
      codeReviewVerdictGlyph('concerns'),
      codeReviewVerdictGlyph('block'),
      codeReviewVerdictGlyph('unknown'),
    ]);
    expect(glyphs.size).toBe(4);
  });
});
