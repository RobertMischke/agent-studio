/**
 * Curated list of orchestrator models a project can be configured with.
 * Empty `id` is the "use default" option (resolves to claude-opus-4-7
 * in the backend). The list is small on purpose: the orchestrator is
 * supposed to make decisions on the user's behalf, so the model needs
 * to be capable; the cheap models are deliberately excluded as
 * orchestrator-models even though they can run as task agents.
 */
export const OrchestratorRunner_KnownModels: readonly { id: string; label: string }[] = [
  { id: '',                  label: 'Default (Opus 4.7)' },
  { id: 'claude-opus-4-7',   label: 'Claude Opus 4.7' },
  { id: 'claude-sonnet-4-6', label: 'Claude Sonnet 4.6 (cheaper)' }
];

/**
 * Models a per-pipeline-step LLM call can be pinned to. Wider than the
 * orchestrator list because a cheap post-step (a quality aspect, a
 * summary) is fine on Haiku, whereas the orchestrator deliberately
 * excludes the cheap tier. Empty `id` clears the override so the step
 * falls back to the project OrchestratorModel and then the runtime
 * default (see PipelineStepConfigResolver.ResolveModel). The price table
 * that turns these into cost lives in backend TokenPricing.
 */
export const PipelineStep_KnownModels: readonly { id: string; label: string; thinkingLevels: readonly string[]; defaultThinkingLevel: string | null }[] = [
  { id: '',                  label: 'Inherit (project / default)', thinkingLevels: [], defaultThinkingLevel: null },
  { id: 'claude-opus-4-7',   label: 'Opus 4.7 (strongest)', thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
  { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
  { id: 'claude-haiku-4-5',  label: 'Haiku 4.5 (cheapest)', thinkingLevels: [], defaultThinkingLevel: null }
];

/** Gate-mode choices for steps that expose a warn/fail gate (lint, decision). */
export const PipelineStep_GateModes: readonly { id: string; label: string }[] = [
  { id: '',     label: 'Default' },
  { id: 'off',  label: 'Off' },
  { id: 'warn', label: 'Warn' },
  { id: 'fail', label: 'Fail' }
];

/**
 * Run-condition choices for pipeline steps. Mirrors the backend
 * `PipelineStepConditions` vocabulary.
 * Empty `id` clears any condition so the step runs whenever it is enabled
 * (equivalent to `always`). The value-bearing tokens (`task-type`, `tag`)
 * require a free-text value entered alongside the select.
 */
export const PipelineStep_Conditions: readonly { id: string; label: string; needsValue?: boolean }[] = [
  { id: '',                label: 'Always (when enabled)' },
  { id: 'never',           label: 'Never' },
  { id: 'on-abort',        label: 'Only on abort' },
  { id: 'on-nonzero-exit', label: 'Only on non-zero exit' },
  { id: 'on-aspect-fail',  label: 'Only when an aspect fails' },
  { id: 'task-type',       label: 'Only for task type...', needsValue: true },
  { id: 'tag',             label: 'Only for tag...',       needsValue: true }
];

/** Condition tokens that require a free-text value entered next to the select. */
export const PipelineStep_ConditionValueTokens: readonly string[] = ['task-type', 'tag'];
