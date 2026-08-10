import type { StructuredTooltip } from 'coding-agent-chat/shared';
import type { PipelineCostSummary, PipelineStepCost } from '../../../../task-pipeline';
import { buildTokenCostTooltip } from '../../../../tokens';

export function formatPipelineCost(usd: number): string {
  if (usd <= 0) return '$0.00';
  if (usd < 0.01) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(2)}`;
}

export function formatPipelineTokens(tokens: number): string {
  if (tokens <= 0) return '—';
  if (tokens < 1000) return String(tokens);
  const scale = tokens < 1_000_000 ? 1000 : 1_000_000;
  const suffix = tokens < 1_000_000 ? 'k' : 'm';
  return `${(tokens / scale).toFixed(1).replace(/\.0$/, '')}${suffix}`;
}

export function buildPipelineStepTokenTooltip(
  label: string,
  cost: PipelineStepCost | null,
): StructuredTooltip | null {
  if (!cost || cost.totalTokens <= 0) return null;
  const source = cost.tokenUsageSource?.trim();
  const context = [
    ...(source ? [`Source: ${source}`] : []),
    `Model: ${cost.model ?? 'unknown'}`,
    `Input: ${formatPipelineTokens(cost.inputTokens)}`,
    `Output: ${formatPipelineTokens(cost.outputTokens)}`,
    `Cache read: ${formatPipelineTokens(cost.cacheReadTokens)}`,
    `Cache creation: ${formatPipelineTokens(cost.cacheCreationTokens)}`,
    `Total: ${formatPipelineTokens(cost.totalTokens)}`,
  ];
  if (cost.modelKnown) {
    context.push(
      '',
      `Input cost: ${formatPipelineCost(cost.inputCostUsd)}`,
      `Output cost: ${formatPipelineCost(cost.outputCostUsd)}`,
      `Cache read cost: ${formatPipelineCost(cost.cacheReadCostUsd)}`,
      `Cache creation cost: ${formatPipelineCost(cost.cacheCreationCostUsd)}`,
    );
  }
  return {
    title: `${label} tokens`,
    body: buildTokenCostTooltip({
      costUsd: cost.costUsd,
      priceKnown: cost.modelKnown,
      totalTokens: cost.totalTokens,
      context: context.join('\n'),
      unpricedRuns: cost.modelKnown ? 0 : 1,
      pricingGaps: cost.pricingGaps,
    }),
  };
}

export function buildPipelineStepCostTooltip(
  label: string,
  cost: PipelineStepCost | null,
): StructuredTooltip | null {
  if (!cost || cost.totalTokens <= 0) return null;
  const context = cost.modelKnown
    ? [
        `Input: ${formatPipelineCost(cost.inputCostUsd)}`,
        `Output: ${formatPipelineCost(cost.outputCostUsd)}`,
        `Cache read: ${formatPipelineCost(cost.cacheReadCostUsd)}`,
        `Cache creation: ${formatPipelineCost(cost.cacheCreationCostUsd)}`,
      ].join('\n')
    : `Model: ${cost.model ?? 'unknown'}`;
  return {
    title: `${label} cost`,
    body: buildTokenCostTooltip({
      costUsd: cost.costUsd,
      priceKnown: cost.modelKnown,
      totalTokens: cost.totalTokens,
      context,
      unpricedRuns: cost.modelKnown ? 0 : 1,
      pricingGaps: cost.pricingGaps,
    }),
  };
}

export function buildPipelineTotalTokenTooltip(
  cost: PipelineCostSummary,
): StructuredTooltip | null {
  if (cost.totalTokens <= 0) return null;
  const context = [
    'Source: SUM of pipeline steps',
    `Input: ${formatPipelineTokens(cost.totalInputTokens)}`,
    `Output: ${formatPipelineTokens(cost.totalOutputTokens)}`,
    `Cache read: ${formatPipelineTokens(cost.totalCacheReadTokens)}`,
    `Cache creation: ${formatPipelineTokens(cost.totalCacheCreationTokens)}`,
    `Total: ${formatPipelineTokens(cost.totalTokens)}`,
    '',
    `Input API price: ${formatPipelineCost(cost.totalInputCostUsd)}`,
    `Output API price: ${formatPipelineCost(cost.totalOutputCostUsd)}`,
    `Cache read API price: ${formatPipelineCost(cost.totalCacheReadCostUsd)}`,
    `Cache creation API price: ${formatPipelineCost(cost.totalCacheCreationCostUsd)}`,
  ];
  if (cost.anyModelUnknown) {
    context.push('One or more steps used a model with no price data; the estimate covers only priced usage.');
  }
  return {
    title: 'Task total tokens (SUM)',
    body: buildTokenCostTooltip({
      costUsd: cost.totalCostUsd,
      priceKnown: !cost.anyModelUnknown,
      totalTokens: cost.totalTokens,
      context: context.join('\n'),
      unpricedRuns: cost.unpricedRuns ?? (cost.anyModelUnknown ? 1 : 0),
      pricingGaps: cost.pricingGaps,
    }),
  };
}

export function buildPipelineTotalCostTooltip(
  cost: PipelineCostSummary,
): StructuredTooltip | null {
  if (cost.totalTokens <= 0) return null;
  const context = [
    `Input: ${formatPipelineCost(cost.totalInputCostUsd)}`,
    `Output: ${formatPipelineCost(cost.totalOutputCostUsd)}`,
    `Cache read: ${formatPipelineCost(cost.totalCacheReadCostUsd)}`,
    `Cache creation: ${formatPipelineCost(cost.totalCacheCreationCostUsd)}`,
  ];
  if (cost.anyModelUnknown) {
    context.push('One or more steps used a model with no price data; the estimate covers only priced usage.');
  }
  return {
    title: 'Task total cost',
    body: buildTokenCostTooltip({
      costUsd: cost.totalCostUsd,
      priceKnown: !cost.anyModelUnknown,
      totalTokens: cost.totalTokens,
      context: context.join('\n'),
      unpricedRuns: cost.unpricedRuns ?? (cost.anyModelUnknown ? 1 : 0),
      pricingGaps: cost.pricingGaps,
    }),
  };
}
