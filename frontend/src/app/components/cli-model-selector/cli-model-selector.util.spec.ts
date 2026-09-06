import { describe, expect, it } from 'vitest';
import type { CliModelInfo } from '../../features/cli';
import { normalizeThinkingLevel } from './cli-model-selector.util';

describe('normalizeThinkingLevel', () => {
  const astra: CliModelInfo = {
    id: 'gpt-6-astra',
    label: 'GPT-6 Astra',
    multiplier: null,
    vendor: 'openai',
    isDefault: false,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'],
    defaultThinkingLevel: 'medium',
  };

  it('accepts catalog-provided levels that are absent from the static ladder', () => {
    expect(normalizeThinkingLevel([astra], astra.id, 'max')).toBe('max');
    expect(normalizeThinkingLevel([astra], astra.id, 'ultra')).toBe('ultra');
  });

  it('uses the catalog default when the requested level is unsupported', () => {
    expect(normalizeThinkingLevel([astra], astra.id, 'minimal')).toBe('medium');
    expect(normalizeThinkingLevel([astra], astra.id, null)).toBe('medium');
  });
});
