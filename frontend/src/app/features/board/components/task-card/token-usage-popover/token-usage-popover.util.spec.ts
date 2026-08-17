import { describe, expect, it } from 'vitest';
import { buildTypeBreakdown } from './token-usage-popover.util';
import type { PipelineCostSummary } from '../../../../task-pipeline';

function summary(steps: PipelineCostSummary['steps']): PipelineCostSummary {
  return {
    steps,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 0,
    totalInputCostUsd: 0, totalOutputCostUsd: 0, totalCacheReadCostUsd: 0, totalCacheCreationCostUsd: 0, totalCostUsd: 0,
    anyModelUnknown: false,
  };
}

describe('buildTypeBreakdown', () => {
  it('groups steps by kind in coding-run-first order and drops zero-token kinds', () => {
    const rows = buildTypeBreakdown(summary([
      { stepId: 'gate', kind: 'orchestrator', modelKnown: true, inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 15, inputCostUsd: 0.01, outputCostUsd: 0, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0.01 },
      { stepId: 'core', kind: 'core', modelKnown: true, inputTokens: 100, outputTokens: 50, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 150, inputCostUsd: 0.02, outputCostUsd: 0.01, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0.03 },
      { stepId: 'tool', kind: 'tool', modelKnown: true, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 0, inputCostUsd: 0, outputCostUsd: 0, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0 },
    ]));
    expect(rows.map((r) => r.kind)).toEqual(['core', 'orchestrator']);
    expect(rows[0]).toEqual({ kind: 'core', label: 'Core agent work', totalTokens: 150, costLabel: '$0.03' });
    expect(rows[1].label).toBe('Decision');
  });

  it('sums multiple steps of the same kind (e.g. several aspect passes)', () => {
    const rows = buildTypeBreakdown(summary([
      { stepId: 'code-quality', kind: 'aspect', modelKnown: true, inputTokens: 40, outputTokens: 6, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 46, inputCostUsd: 0.01, outputCostUsd: 0, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0.01 },
      { stepId: 'requirement-fit', kind: 'aspect', modelKnown: true, inputTokens: 30, outputTokens: 4, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 34, inputCostUsd: 0.01, outputCostUsd: 0, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0.01 },
    ]));
    expect(rows).toEqual([{ kind: 'aspect', label: 'Aspect', totalTokens: 80, costLabel: '$0.02' }]);
  });

  it('marks a kind unpriced when any of its contributing steps has no resolved price', () => {
    const rows = buildTypeBreakdown(summary([
      { stepId: 'core', kind: 'core', modelKnown: false, inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 15, inputCostUsd: 0, outputCostUsd: 0, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0 },
    ]));
    expect(rows[0].costLabel).toBe('- no price data');
  });
});
