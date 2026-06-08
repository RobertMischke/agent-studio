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
