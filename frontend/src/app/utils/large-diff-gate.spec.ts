import { describe, it, expect } from 'vitest';
import {
  measureDiff,
  isLargeDiff,
  describeDiffSize,
  LARGE_DIFF_LINE_THRESHOLD,
  LARGE_DIFF_BYTE_THRESHOLD,
} from './large-diff-gate';

describe('large-diff-gate', () => {
  it('measures empty / nullish diffs as zero', () => {
    expect(measureDiff('')).toEqual({ lines: 0, bytes: 0 });
    expect(measureDiff(null)).toEqual({ lines: 0, bytes: 0 });
    expect(measureDiff(undefined)).toEqual({ lines: 0, bytes: 0 });
  });

  it('counts lines and bytes', () => {
    expect(measureDiff('a\nb\nc')).toEqual({ lines: 3, bytes: 5 });
    expect(measureDiff('single line')).toEqual({ lines: 1, bytes: 11 });
  });

  it('treats a small diff as not large', () => {
    const small = Array.from({ length: 50 }, (_, i) => `+line ${i}`).join('\n');
    expect(isLargeDiff(small)).toBe(false);
  });

  it('gates on the line threshold', () => {
    const many = Array.from({ length: LARGE_DIFF_LINE_THRESHOLD }, () => '+x').join('\n');
    expect(measureDiff(many).lines).toBeGreaterThanOrEqual(LARGE_DIFF_LINE_THRESHOLD);
    expect(isLargeDiff(many)).toBe(true);
  });

  it('gates on the byte threshold for few-line, huge-byte diffs (minified)', () => {
    const oneHugeLine = '+' + 'x'.repeat(LARGE_DIFF_BYTE_THRESHOLD + 10);
    expect(measureDiff(oneHugeLine).lines).toBe(1);
    expect(isLargeDiff(oneHugeLine)).toBe(true);
  });

  it('formats a human-readable size label', () => {
    expect(describeDiffSize('a\nb\nc')).toBe('3 lines · 5 B');
    const kb = 'x'.repeat(2048);
    expect(describeDiffSize(kb)).toBe('1 line · 2 KB');
  });
});
