/**
 * Pure helpers + vocabulary for the project-level Pipeline page. Kept out
 * of the component so the ordering / sectioning logic stays testable and
 * the component controller stays under its size budget. Nothing here
 * touches Angular — it operates on the catalogue + order arrays directly.
 */
import type { PipelineCatalogueStep } from '../../../task-pipeline';
import type { PipelineStepKindKey } from '../../../project-token-usage';

/** Gate-mode choices for steps that expose a warn/fail gate (lint, decision). */
export const PIPELINE_GATE_MODES: readonly { id: string; label: string }[] = [
  { id: '',     label: 'Default' },
  { id: 'off',  label: 'Off' },
  { id: 'warn', label: 'Warn' },
  { id: 'fail', label: 'Fail' },
];

/**
 * Run-condition choices for pipeline steps. Mirrors the backend
 * `PipelineStepConditions` vocabulary. Empty `id` clears any condition so
 * the step runs whenever it is enabled (equivalent to `always`). The
 * value-bearing tokens (`task-type`, `tag`) require a free-text value.
 */
export const PIPELINE_CONDITIONS: readonly { id: string; label: string; needsValue?: boolean }[] = [
  { id: '',                label: 'Always (when enabled)' },
  { id: 'never',           label: 'Never' },
  { id: 'on-abort',        label: 'Only on abort' },
  { id: 'on-nonzero-exit', label: 'Only on non-zero exit' },
  { id: 'on-aspect-fail',  label: 'Only when an aspect fails' },
  { id: 'task-type',       label: 'Only for task type...', needsValue: true },
  { id: 'tag',             label: 'Only for tag...',       needsValue: true },
];

/** Condition tokens that require a free-text value entered next to the select. */
export const PIPELINE_CONDITION_VALUE_TOKENS: readonly string[] = ['task-type', 'tag'];

/** One row per configurable step: catalogue metadata joined with the override. */
export interface PipelineAdminRow {
  id: string;
  displayName: string;
  kind: string;
  usesModel: boolean;
  usesPrompt: boolean;
  supportsMode: boolean;
  canDisable: boolean;
  supportsCondition: boolean;
  phase: string;
  enabled: boolean;
  cliType: string;
  model: string;
  thinkingLevel: string;
  /** Inline prompt override text (legacy). Empty = bound to the registry template. */
  prompt: string;
  /** Registry template this step renders from, when the catalogue declares one. */
  promptTemplate: string;
  mode: string;
  condition: string;
  conditionValue: string;
  conditionNeedsValue: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
}

/** One pre/core/post phase grouping for the grid. */
export interface PipelineGroup {
  phase: string;
  label: string;
  rows: PipelineAdminRow[];
}

/** One step-kind window rollup for the cost legend. */
export interface PipelineKindLegendRow {
  kind: PipelineStepKindKey;
  label: string;
  tokens: number;
  cost: number;
  anyUnknown: boolean;
}

const PIPELINE_KIND_LABELS: Record<PipelineStepKindKey, string> = {
  core: 'Core agent work',
  aspect: 'Aspects',
  tool: 'Tool steps',
  orchestrator: 'Orchestrator',
  drift: 'Drift',
  module: 'Modules',
};

export function kindLabel(kind: PipelineStepKindKey): string {
  return PIPELINE_KIND_LABELS[kind] ?? kind;
}

export function phaseForStep(step: PipelineCatalogueStep): string {
  if (step.kind === 'aspect') return 'aspect';
  if (step.kind === 'tool') return 'tool';
  if (step.kind === 'drift') return 'drift';
  if (step.kind === 'core') return 'core';
  if (step.id.startsWith('pre-')) return 'pre';
  if (step.id.includes('decision')) return 'decision';
  if (step.id.includes('abort')) return 'abort';
  return 'post';
}

export function pipelinePhaseLabel(phase: string): string {
  switch (phase) {
    case 'pre': return 'Pre steps';
    case 'core': return 'Core agent work';
    case 'aspect': return 'Aspect reviews';
    case 'tool': return 'Tool steps';
    case 'decision': return 'Decision';
    case 'drift': return 'Drift';
    case 'abort': return 'Abort-only';
    default: return 'Post steps';
  }
}

/** The pre / core / post ordering bucket a step belongs to. */
export function pipelineOrderSection(step: PipelineCatalogueStep): 'pre' | 'core' | 'post' {
  if (step.kind === 'core') return 'core';
  if ((step.phase ?? phaseForStep(step)) === 'pre') return 'pre';
  return 'post';
}

/** Apply the persisted per-project step order to one section's steps. */
export function sortPipelineOrderSection(
  steps: readonly PipelineCatalogueStep[],
  order: readonly string[],
): readonly PipelineCatalogueStep[] {
  if (order.length === 0 || steps.length <= 1) return steps;

  const rank = new Map<string, number>();
  for (const id of order) {
    const key = id.trim().toLowerCase();
    if (key && !rank.has(key)) rank.set(key, rank.size);
  }
  if (rank.size === 0) return steps;

  return steps
    .map((step, index) => ({ step, index, rank: rank.get(step.id.toLowerCase()) ?? Number.MAX_SAFE_INTEGER }))
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map(x => x.step);
}

/** Catalogue re-ordered as pre (ordered) + core (fixed) + post (ordered). */
export function orderedPipelineCatalogue(
  steps: readonly PipelineCatalogueStep[],
  order: readonly string[],
): readonly PipelineCatalogueStep[] {
  const pre = sortPipelineOrderSection(steps.filter(s => pipelineOrderSection(s) === 'pre'), order);
  const core = steps.filter(s => pipelineOrderSection(s) === 'core');
  const post = sortPipelineOrderSection(steps.filter(s => pipelineOrderSection(s) === 'post'), order);
  return [...pre, ...core, ...post];
}

/** Whether the step at `index` can swap with a same-section neighbour in `direction`. */
export function canMovePipelineStep(
  steps: readonly PipelineCatalogueStep[],
  index: number,
  direction: -1 | 1,
): boolean {
  const step = steps[index];
  if (!step) return false;
  const section = pipelineOrderSection(step);
  if (section === 'core') return false;

  let target = index + direction;
  while (target >= 0 && target < steps.length) {
    if (pipelineOrderSection(steps[target]) === section) return true;
    target += direction;
  }
  return false;
}

/** Theoretical USD cost. Sub-cent values still read as a number, not "$0.00". */
export function formatCost(usd: number | null | undefined): string {
  const v = usd ?? 0;
  if (v <= 0) return '$0.00';
  if (v < 0.01) return `$${v.toFixed(4)}`;
  if (v < 1) return `$${v.toFixed(3)}`;
  return `$${v.toFixed(2)}`;
}

export function formatTokens(n: number | null | undefined): string {
  const v = n ?? 0;
  const sign = v < 0 ? '-' : '';
  const abs = Math.abs(v);
  if (abs >= 1_000_000_000) return `${sign}${(abs / 1_000_000_000).toFixed(1)}B`;
  if (abs >= 1_000_000) return `${sign}${(abs / 1_000_000).toFixed(1)}M`;
  if (abs >= 1_000) return `${sign}${(abs / 1_000).toFixed(1)}k`;
  return `${sign}${abs}`;
}
