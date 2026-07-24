import { describe, expect, it } from 'vitest';
import {
  TOKEN_COST_ESTIMATE_NOTICE,
  buildTokenCostTooltip,
} from './token-cost-tooltip.util';

describe('token cost tooltip', () => {
  it('renders a priced historical estimate with the mandatory caveat', () => {
    const tooltip = buildTokenCostTooltip({
      costUsd: 1.234,
      priceKnown: true,
      context: '12,000 tokens at execution time.',
    });

    expect(tooltip).toContain('Estimated cost: $1.23');
    expect(tooltip).toContain('at execution time');
    expect(tooltip).toContain(TOKEN_COST_ESTIMATE_NOTICE);
  });

  it('says no price data instead of presenting a silent zero', () => {
    const tooltip = buildTokenCostTooltip({ costUsd: null, priceKnown: false });

    expect(tooltip).toContain('Estimated cost: no price data');
    expect(tooltip).toContain(TOKEN_COST_ESTIMATE_NOTICE);
  });

  it('marks an aggregate with priced and unpriced calls as partial', () => {
    expect(buildTokenCostTooltip({ costUsd: 0.5, priceKnown: false }))
      .toContain('$0.50 (partial; some usage has no price data)');
  });
});
