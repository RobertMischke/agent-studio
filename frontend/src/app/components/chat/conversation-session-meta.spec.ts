import { describe, expect, it } from 'vitest';
import { parseRateLimit, windowLabelFor } from './conversation-session-meta';

describe('windowLabelFor', () => {
  it('maps known window tokens to friendly labels', () => {
    expect(windowLabelFor('five_hour')).toBe('5h');
    expect(windowLabelFor('five-hour')).toBe('5h');
    expect(windowLabelFor('weekly')).toBe('Weekly');
    expect(windowLabelFor('monthly')).toBe('Monthly');
  });

  it('title-cases unknown tokens and returns undefined for empty input', () => {
    expect(windowLabelFor('rolling_window')).toBe('Rolling Window');
    expect(windowLabelFor(undefined)).toBeUndefined();
  });
});

describe('parseRateLimit', () => {
  it('lifts window, status and resetsAt out of the bracket payload', () => {
    const raw =
      '● Rate limit · five-hour · allowed · reset in 4.4 h  ' +
      '[window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false]';
    const parsed = parseRateLimit(raw);

    expect(parsed.window).toBe('five_hour');
    expect(parsed.windowLabel).toBe('5h');
    expect(parsed.status).toBe('allowed');
    expect(parsed.resetsAtMs).toBe(1777393800 * 1000);
    expect(parsed.resetHint).toBe('reset in 4.4 h');
    expect(parsed.raw).toBe(raw.trim());
  });

  it('tolerates a line with no bracket payload, keeping the human hint', () => {
    const parsed = parseRateLimit('● Rate limit · reset in 12 m');
    expect(parsed.window).toBeUndefined();
    expect(parsed.status).toBeUndefined();
    expect(parsed.resetsAtMs).toBeUndefined();
    expect(parsed.resetHint).toBe('reset in 12 m');
  });

  it('ignores a non-positive resetsAt value', () => {
    const parsed = parseRateLimit('● Rate limit [window=weekly status=limited resetsAt=0]');
    expect(parsed.windowLabel).toBe('Weekly');
    expect(parsed.status).toBe('limited');
    expect(parsed.resetsAtMs).toBeUndefined();
  });
});
