/** Mandatory caveat shown on every token-to-money tooltip. */
export const TOKEN_COST_ESTIMATE_NOTICE =
  'Estimated - historical list prices; discounts and provider-side caching adjustments are not considered.';

export interface TokenCostTooltipOptions {
  costUsd: number | null | undefined;
  /** True only when every usage row contributing to the amount was priced. */
  priceKnown: boolean;
  /** Optional token/context sentence rendered before the money estimate. */
  context?: string | null;
}

/** Consistent compact USD formatting for token-cost tooltips and popovers. */
export function formatTokenCostUsd(costUsd: number): string {
  if (!Number.isFinite(costUsd)) return 'no price data';
  const digits = Math.abs(costUsd) > 0 && Math.abs(costUsd) < 0.01 ? 4 : 2;
  return `$${costUsd.toFixed(digits)}`;
}

/**
 * Shared token-cost tooltip contract. Unknown prices never become a silent
 * zero, partial aggregates remain explicit, and the estimate caveat is always
 * present.
 */
export function buildTokenCostTooltip(options: TokenCostTooltipOptions): string {
  const lines: string[] = [];
  if (options.context?.trim()) lines.push(options.context.trim());

  const hasCost = Number.isFinite(options.costUsd);
  if (options.priceKnown && hasCost) {
    lines.push(`Estimated cost: ${formatTokenCostUsd(options.costUsd!)}`);
  } else if (hasCost && options.costUsd! > 0) {
    lines.push(`Estimated cost: ${formatTokenCostUsd(options.costUsd!)} (partial; some usage has no price data)`);
  } else {
    lines.push('Estimated cost: no price data');
  }
  lines.push(TOKEN_COST_ESTIMATE_NOTICE);
  return lines.join('\n');
}
