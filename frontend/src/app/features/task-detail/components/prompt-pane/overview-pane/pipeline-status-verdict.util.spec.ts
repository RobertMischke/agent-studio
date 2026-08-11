import { describe, expect, it } from 'vitest';
import { distinctStepVerdict } from './pipeline-status-verdict.util';

describe('distinctStepVerdict', () => {
  it.each([
    ['failed', 'ERROR'],
    ['failed', 'fail'],
    ['passed', 'PASS'],
    ['passed', 'succeeded'],
    ['skipped', 'skipped'],
    ['disabled', 'inactive'],
  ])('drops %s/%s because the status icon already carries it', (status, verdict) => {
    expect(distinctStepVerdict(status, verdict)).toBeNull();
  });

  it.each([
    ['failed', 'needsinput'],
    ['passed', 'concerns'],
    ['passed', 'attempt 2'],
    ['failed', 'loop-detected'],
  ])('keeps %s/%s because it adds routing or result detail', (status, verdict) => {
    expect(distinctStepVerdict(status, verdict)).toBe(verdict);
  });
});
