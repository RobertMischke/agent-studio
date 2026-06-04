/**
 * Wire types for `GET /api/tasks/{id}/pipeline`. Mirrors the backend
 * `TaskPipeline` / `PipelineExecutionRecord` / `PipelineCostSummary`
 * shapes (System.Text.Json camelCases property names and serialises the
 * enums as camelCase strings via the global `JsonStringEnumConverter`).
 */

export type StepKind = 'module' | 'core' | 'aspect' | 'orchestrator' | 'tool' | 'drift';
export type StepRunMode = 'sequential' | 'parallel';
export type PipelineStepStatus =
  | 'pending'
  | 'running'
  | 'passed'
  | 'failed'
  | 'skipped'
  | 'planned';

/** Static metadata for one step in the pipeline catalogue. */
export interface PipelineStep {
  id: string;
  displayName: string;
  kind: StepKind;
  runMode: StepRunMode;
  dependsOn: string[];
  model?: string | null;
  cliType?: string | null;
  timeoutMs?: number | null;
  idempotent: boolean;
  stub: boolean;
}

/** The full static pipeline definition this job targets. */
export interface TaskPipeline {
  id: string;
  displayName: string;
  version: number;
  pre: PipelineStep[];
  core: PipelineStep[];
  post: PipelineStep[];
  /** Flattened pre+core+post in order (backend computed property). */
  allSteps?: PipelineStep[];
}

/** One recorded step execution from `pipeline-execution.json`. */
export interface PipelineStepExecution {
  stepId: string;
  kind: StepKind;
  model?: string | null;
  status: PipelineStepStatus;
  startedAt?: string | null;
  completedAt?: string | null;
  durationMs: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  reason?: string | null;
  verdict?: string | null;
  /**
   * Human-readable concern detail for aspect steps with a non-pass
   * verdict, lifted from `aspect-{id}.md` frontmatter by the backend.
   * Drives the tooltip on the CONCERNS pill in the Overview pipeline.
   */
  verdictSummary?: string | null;
}

/** The persisted execution record (null when the job never ran a pipeline). */
export interface PipelineExecutionRecord {
  pipelineId: string;
  pipelineVersion: number;
  jobId: string;
  project: string;
  startedAt: string;
  completedAt?: string | null;
  steps: PipelineStepExecution[];
  /**
   * 1-based run counter. A re-run / re-issue starts a fresh record and
   * increments this; anything above 1 means the pipeline was restarted, so
   * the Overview pipeline can flag it as a new run.
   */
  attempt?: number;
  /**
   * Prior completed runs for this job, most-recent first, so old step runs
   * stay distinguishable from the current ones after a restart. Each entry
   * keeps its own `steps` but carries an empty `previousAttempts` (the chain
   * is flattened, not nested) and is bounded to the last few runs.
   */
  previousAttempts?: PipelineExecutionRecord[];
}

/** Derived per-step cost (USD) for one recorded step. */
export interface PipelineStepCost {
  stepId: string;
  kind: StepKind;
  model?: string | null;
  /** False when the model is not in the price table -> render "n/a". */
  modelKnown: boolean;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  totalTokens: number;
  costUsd: number;
}

/** Per-step rows plus the task total. */
export interface PipelineCostSummary {
  steps: PipelineStepCost[];
  totalTokens: number;
  totalCostUsd: number;
  anyModelUnknown: boolean;
}

/** Per-project override resolved for one step (from project-settings.json). */
export interface PipelineStepConfig {
  enabled: boolean;
  model?: string | null;
  mode?: string | null;
}

/**
 * One row in `GET /api/projects/pipeline-catalogue`. The capability flags
 * tell the Settings UI which controls a step accepts: a model picker
 * (`usesModel`), a gate-mode select (`supportsMode`), an enable toggle
 * (`canDisable`).
 */
export interface PipelineCatalogueStep {
  id: string;
  displayName: string;
  kind: StepKind;
  usesModel: boolean;
  supportsMode: boolean;
  canDisable: boolean;
  /**
   * Initial toggle state when the project has no explicit override. Drift
   * post-steps ship `false` (opt-in, expensive); every other step is `true`.
   */
  defaultEnabled: boolean;
  /**
   * Whether the runtime evaluates a per-step run condition for this step.
   * Only the abort-review step honours conditions today, so it is the only
   * row that renders the condition control.
   */
  supportsCondition: boolean;
}

/**
 * Per-step run condition: a `when` token plus an optional `value` used by the
 * value-bearing tokens (`task-type`, `tag`). An absent condition (or `always`)
 * means "run whenever the step is enabled".
 */
export interface PipelineStepCondition {
  when: PipelineStepConditionToken;
  value?: string | null;
}

/** Run-condition vocabulary, mirrors backend `PipelineStepConditions`. */
export type PipelineStepConditionToken =
  | 'always'
  | 'never'
  | 'on-abort'
  | 'on-nonzero-exit'
  | 'on-aspect-fail'
  | 'task-type'
  | 'tag';

/** Envelope of `GET /api/projects/pipeline-catalogue`. */
export interface PipelineCatalogue {
  pipelineId: string;
  steps: PipelineCatalogueStep[];
}

/**
 * Raw per-step override as persisted in project-settings.json (the value
 * type of the `pipelineSteps` map on the project-settings projection). All
 * fields optional; an absent field means "fall through to the built-in
 * default". `enabled` is nullable on the wire (null/true both mean on).
 */
export interface PipelineStepSetting {
  enabled?: boolean | null;
  mode?: string | null;
  model?: string | null;
  condition?: PipelineStepCondition | null;
}

/** Full response envelope of the pipeline read endpoint. */
export interface TaskPipelineResponse {
  pipeline: TaskPipeline;
  execution: PipelineExecutionRecord | null;
  cost: PipelineCostSummary;
  config: Record<string, PipelineStepConfig>;
}
