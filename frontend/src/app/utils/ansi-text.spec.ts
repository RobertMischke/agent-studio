import { describe, expect, it } from 'vitest';
import { stripAnsi } from './ansi-text';

describe('stripAnsi', () => {
  it('removes escaped and orphaned colour sequences', () => {
    expect(stripAnsi('\u001b[33mBuilding...\u001b[39m')).toBe('Building...');
    expect(stripAnsi('[33m[39m Building...')).toBe(' Building...');
  });

  it('keeps ordinary bracketed text', () => {
    expect(stripAnsi('[build] Building package 33')).toBe('[build] Building package 33');
  });
});
