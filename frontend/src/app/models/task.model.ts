export type CliType = 'copilot' | 'claude' | 'codex' | 'gemini';
export const CLI_TYPES: CliType[] = ['copilot', 'claude', 'codex', 'gemini'];

/**
 * Single source of truth for lane / task-state keys. Mirrors backend
 * `TaskStates` (src/AgentTaskboard.Shared/Models/TaskModels.cs) one-for-one.
 * Every lane literal in frontend prod code routes through this constant so a
 * future lane rename is a two-place value change (here + backend `TaskStates`)
 * plus a data migration, not a 27-file string hunt.
 */
export const TaskState = {
  Backlog: '0-backlog',
  Preparation: '1-preparation',
  OrchestratorPrep: '1a-orchestrator-prep',
  Ready: '2-ready',
  Progress: '3-progress',
  FailedPickup: '3a-failed-pickup',
  CodeNotComplete: '3b-code-not-complete',
  AutoReview: '4-auto-review',
  HumanReview: '5-human-review',
  Escalated: '5e-escalated',
  Completed: '6-completed',
  Archive: '7-archive',
} as const;

/** Union of the canonical lane-key string literals. */
export type TaskStateKey = (typeof TaskState)[keyof typeof TaskState];

/** All canonical lane keys, in board order. */
export const ALL_TASK_STATES: readonly TaskStateKey[] = Object.values(TaskState);

// Cycle 9i: this file is the canonical "shared kernel" — TaskInfo,
// TaskDetail, GroupedJobs, CliExecution, etc. Feature-specific types
// (git, tokens, orchestrator, screenshots, claude, run-timeline,
// session-events, project-chat, roadmap, quota, cli, project-token-usage)
// live under their own `features/X/models/` and are accessed via the
// feature barrel. The two `import type` lines below let TaskInfo's
// own field types reference feature-owned shapes without copying them.
import type { TaskCommitInfo, TaskProvenanceRecord } from '../features/git';
import type { TaskTokenSummary } from '../features/tokens';
import type { OrchestratorLogEntry, OrchestratorSession } from '../features/orchestrator';

/** One row in `logs/session-events.jsonl` for a job. */
// (SessionEvent + SessionEventsResponse now in features/session-events/models; re-exported below)

/**
 * One CLI invocation between two user inputs - the unit of conversation
 * the protocol-pane run timeline renders. Backed by RunRecord on the
 * backend (see backend/Services/Runner/RunTimeline.cs). lineStart /
 * lineEnd are 1-based indices into cli-output.log so the drill-down
 * activity-log filter does not have to re-derive the boundaries.
 */
// (Run timeline / commits / files / diff now in features/run-timeline/models; re-exported below)

// (GitProjectSummary, GitHygieneStatus, TaskHygieneContext now in features/git/models/git.model.ts; re-exported above)

/** Card kind: `epic` is a container for sub-tasks; `task` is an ordinary card. */
export type TaskKind = 'task' | 'epic';

/**
 * Task execution mode. Mirrors backend `TaskModes`. `coding` is the default
 * read-write mode; `planning` and `research` are read-only (no source writes),
 * and `research` additionally permits web access by default.
 */
export type TaskMode = 'coding' | 'planning' | 'research';

/**
 * F34 — structured cross-references between tasks, keyed by F33 stable keys
 * (e.g. `ATP-19`). Mirrors backend `TaskReferences`. Four relation kinds:
 * `dependsOn` (target must be complete before this is workable — a DAG edge),
 * `relatedTo` (thematic, non-blocking), `blockedBy` (currently blocked),
 * `supersedes` (this task replaces an obsolete target).
 */
export interface TaskReferences {
  dependsOn: string[];
  relatedTo: string[];
  blockedBy: string[];
  supersedes: string[];
}

/** The four F34 relation kinds, in display order. */
export type TaskReferenceKind = 'dependsOn' | 'relatedTo' | 'blockedBy' | 'supersedes';
export const TASK_REFERENCE_KINDS: TaskReferenceKind[] = [
  'dependsOn',
  'relatedTo',
  'blockedBy',
  'supersedes',
];

/**
 * One incoming reference returned by `GET /api/tasks/{id}/dependents`.
 * Mirrors backend `TaskReferenceLink`: a task (`sourceJobId` / `sourceKey`)
 * points at the queried key via `kind`. Carries enough of the source task to
 * render a chip and route to it without a second lookup.
 */
export interface TaskReferenceLink {
  sourceKey: string | null;
  sourceJobId: string;
  sourceTitle: string;
  sourceState: string;
  sourceWatchPath: string;
  kind: TaskReferenceKind | string;
}

export interface TaskInfo {
  id: string;
  taskKey: string;
  key?: string | null;
  displayKey?: string | null;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: string | null;
  /**
   * Per-job orchestrator token rollup. The kanban card renders a small
   * colour-tiered bubble (2.4k / 850k / 3.1M) when this is non-null and
   * the total is greater than zero, with a hover popover showing the
   * detailed breakdown.
   */
  tokenSummary?: TaskTokenSummary | null;
  model: string | null;
  thinkingLevel?: string | null;
  cliType: CliType | null;
  /**
   * Card kind. `epic` cards are containers for sub-tasks; `task` (the default
   * when omitted) is an ordinary card. See backend `TaskKinds`.
   */
  kind?: TaskKind;
  /**
   * Parent epic id when this card is a sub-task of an epic, else null/absent.
   * Set at create time (way 1), post-hoc via PUT /api/tasks/{id}/epic (way 2),
   * or by an epic's decomposition run (way 3).
   */
  epicId?: string | null;
  /**
   * Execution mode. Mirrors backend `TaskInfo.Mode` (default `coding`).
   * Older payloads may omit it, so callers treat absent as `coding`.
   */
  mode?: TaskMode;
  /** Whether the agent may use the web during this task. Mirrors backend `AllowWebAccess`. */
  allowWebAccess?: boolean;
  useOwnSession: boolean | null;
  /**
   * Per-task context-mode override (T1b / ASS-1742): `'clean'` (isolated
   * per-run CLI home) or `'shared'` (the operator's global CLI state). Absent /
   * null means no task override — the run falls back to the project setting and
   * then the platform default (`clean`). Mirrors backend `TaskInfo.ContextMode`.
   */
  contextMode?: string | null;
  lastUsage: SessionUsage | null;
  execution: CliExecution | null;
  commit: TaskCommitInfo | null;
  /**
   * Ordered chain of commits attributed to this task (oldest -> newest).
   * Tasks regularly produce more than one commit across iterations
   * (continue-mode follow-up, crash-recovery + repair, operator-driven
   * steers). Backwards compat: when only the legacy singular `commit`
   * is on disk, the backend surfaces it here as `[commit]`.
   */
  commits?: TaskCommitInfo[];
  /**
   * Cheap "did any run move HEAD / was an auto-commit stamped" signal from
   * the scanner. Lets the card disambiguate a card with zero `commits[]`:
   * `false` -> analysis-only task, render the explicit "no code changes"
   * badge; `true` -> work landed but the attributed chain is still empty,
   * render the "commit discovery pending" diagnostic instead. Never a
   * count - the commit total derives strictly from `commits[]` (SSOT).
   */
  codeActivityDetected?: boolean;
  /** Saved user intent waiting for the auto-pickup loop. Surfaces in the UI as a ⏳ badge. */
  pendingIntent?: PendingIntent | null;
  /**
   * Auto-mode "stuck loop" snapshot - populated only while the orchestrator is
   * actively answering NEEDS_INPUT for this job. Mirrors backend
   * `AutoLoopSnapshot`. The card shows a "auto-loop N/M" badge so the user
   * can see how much of the budget the orchestrator has spent before the
   * circuit breaker stops it.
   */
  autoLoop?: AutoLoopSnapshot | null;
  /**
   * Live state of the post-completion summary (Haiku) call. Populated only
   * while the summarizer is generating or just finished. The card shows an
   * "auto-reviewing" pill so the user knows the orchestrator is still
   * working on a card that just landed in 4-review.
   */
  summaryState?: TaskSummaryState | null;
  /**
   * Latest categorized runner-outcome issue, derived from logs/cli-output.log.
   * This is read-only observability: it surfaces permission blocks, watchdog
   * timeouts, missing sentinels, and classifier ambiguity without creating a
   * second persistence path beside the existing task log.
   */
  outcomeIssue?: TaskOutcomeIssue | null;
  /**
   * Latest orchestrator-review verdict for this job, sourced from the
   * per-project decision journal. Drives the 4-review kanban swim-lane
   * subdivision (orchestrator-review vs human-review) and the workspace
   * top-banner. Values: `pending`, `reissue`, `escalate`, `accept`. Null
   * when no orchestrator decision exists for this card.
   */
  orchestratorVerdict?: 'pending' | 'reissue' | 'escalate' | 'accept' | null;
  /**
   * Client identity that owns this job. References ClientIdentity.id.
   * Defaults to `local-default` for legacy jobs whose `job.json` predates
   * per-task attribution. The frontend renders a small chip on the card
   * (emoji + colour) so reviewers can see at a glance who is responsible.
   */
  ownerClientId?: string | null;
  /**
   * Optional lifecycle substate. Mirrors backend `TaskInfo.Phase`. Drives
   * the kanban Ready group split (Human Ready vs Intake) and the per-card
   * phase chip. Null means "no explicit phase on disk"; the Ready lane
   * defaults to Human Ready in that case (compatibility contract from
   * docs/research/expanded-lifecycle-lanes-plan-2026-05.md).
   *
   * Allowed values for 2-ready: `human-ready`, `intake-running`,
   * `intake-blocked`, `intake-passed`. The 3-progress phase values
   * (`execution-running`, `post-processing-running`, ...) ride on the
   * same field but are owned by the post-processing slice.
   */
  phase?: string | null;
  /**
   * Structural classification of the task. One of `bug`, `feature`, or
   * `chore` (default for legacy and technical work). Drives the small chip
   * rendered on the kanban card and the type filter pill in the header.
   * Legacy `user-story` values on disk are normalised to `feature` server-side
   * on read.
   */
  taskType?: string;
  /**
   * Workspace-level tag ids attached to this job. Look up display label /
   * colour in the registry served at GET /api/tags. Unknown ids (entries
   * that were soft-deleted from the registry) render as a faint ghost chip.
   */
  tags?: string[];
  /**
   * F34 cross-references to other tasks by F33 stable key. Always present
   * (backend surfaces an empty instance when absent on disk). Drives the
   * detail-view reference section and the card `waiting on KEY` badge.
   */
  references?: TaskReferences;
  /**
   * Append-only commit-provenance record (ASS-1724). Mirrors backend
   * `TaskInfo.Provenance` and ships on every board card so the git-state pill
   * can show *where the work actually lives* (active `task/<id>` worktree vs
   * landed in develop vs sequential main-checkout) from ground truth instead of
   * guessing from the lane. Null on legacy `task.json` that predate the field.
   */
  provenance?: TaskProvenanceRecord | null;

  /**
   * ASS-1751: read-time run-activity classification for `3-progress` cards,
   * distinguishing a live run, a failed run waiting out the rapid-crash
   * backoff, and an orphan killed by a backend restart. Present only on
   * Progress-lane tasks; null/absent on every other lane. Pure visibility.
   */
  runActivity?: TaskRunActivity | null;
}

export interface TaskOutcomeIssue {
  kind:
    | 'permission-blocked'
    | 'watchdog-timeout'
    | 'missing-terminal-sentinel'
    | 'classifier-unknown'
    | 'heuristic-done'
    | 'environment-blocker'
    | string;
  label: string;
  severity: 'Info' | 'Warn' | 'High' | string;
  summary: string;
  lastSeenAt: string | null;
}

/**
 * One entry returned by GET /api/clients. Keep in sync with the backend
 * `ClientSummary` record.
 */
export interface ClientSummary {
  id: string;
  displayName: string;
  emoji: string | null;
  colour: string | null;
  kind: 'human' | 'agent-instance' | 'external-tool' | 'service' | 'retired';
  registeredAt: string;
  lastSeenAt: string | null;
  tokenBudgetMonthly: number | null;
  notes: string | null;
  defaultCliType?: string | null;
  defaultModel?: string | null;
  defaultThinkingLevel?: string | null;
}

/**
 * Body returned by GET/PUT /api/clients/{id}/defaults. Keep in sync with the
 * backend `ClientDefaultsResponse` record.
 */
export interface ClientDefaultsResponse {
  id: string;
  defaultCliType: string | null;
  defaultModel: string | null;
  defaultThinkingLevel: string | null;
}

/**
 * Per-job orchestrator token rollup. Mirrors backend `TaskTokenSummary`.
 * Surfaced on the kanban card as a colour-tiered "token bubble" with a
 * hover popover that lists per-call rows.
 */
// (TaskTokenSummary, TaskTokenCall now in features/tokens/models/tokens.model.ts; re-exported below)

export interface AutoLoopSnapshot {
  iteration: number;
  maxIterations: number;
  tokensUsed: number;
  maxTokens: number;
  startedAt: string;
  lastAt: string;
  lastQuestion?: string | null;
  lastReply?: string | null;
  lastError?: string | null;
}

// (TaskCommitInfo, TaskCommitDetail now in features/git/models/git.model.ts; re-exported above)

export interface SessionUsage {
  at: string;
  tokens: string | null;
  changes: string | null;
  requests: string | null;
}

// (CliModelInfo, CliModelCatalog, CopilotModelInfo, CopilotModelCatalog,
// CliSessionInfo, CliUsageProjectGroup, CliUsageSection, CliUsageReport
// now in features/cli/models/cli.model.ts; re-exported below.)

export type TaskSummaryStatus = 'none' | 'generating' | 'ready' | 'failed';

/**
 * How a follow-up sent through the chat box should be interpreted by the
 * runner. Mirrors backend ContinueModes.
 *
 * - continue: next conversation turn, default.
 * - steer:    course correction; the agent overrides its current plan.
 * - extend:   additive extension; backend writes prompt-N.md and the agent
 *             treats the original task plus the new prompt as the full job.
 * - newTask:  new sub-task in the same session; prior context preserved but
 *             the request is new.
 */
export type ContinueMode = 'continue' | 'steer' | 'extend' | 'newTask';

/**
 * Saved user intent waiting for the auto-pickup loop to run. Populated when
 * the user sends a follow-up to a job that is not the project's current
 * active job; the project busy at the time saved this draft on disk.
 * Mirrors backend `PendingIntent`.
 */
export interface PendingIntent {
  version: number;
  mode: ContinueMode;
  prompt: string;
  savedAt: string;
  savedReason: string;
  savedAgainstActiveJobId: string | null;
}

/**
 * Discriminated response for `POST /api/tasks/{id}/continue` and `/start`.
 * `started` means the run is live; `queued` means the project was busy
 * with another job, the user's intent has been saved on the target task,
 * and the target task is now at the top of `2-ready`. The frontend treats
 * `queued` as success-with-info (no modal); the chat carries the
 * orchestrator's `[queued]` line.
 */
export interface ContinueTaskResponse {
  status: 'started' | 'queued';
  execution?: CliExecution | null;
  queued?: ContinueTaskQueuedInfo | null;
}

export interface ContinueTaskQueuedInfo {
  reason: 'project-busy';
  activeJobId?: string | null;
  activeJobTitle?: string | null;
  position: number;
  promotedFromState?: string | null;
}

/**
 * One entry in the orchestrator log feed for a project. Mirrors backend
 * `OrchestratorLogEntry`. Kinds: decision / action / observation /
 * intervention. Topics group entries in the UI feed.
 */
// (OrchestratorLogEntry/TokenUsage/Response now in features/orchestrator/models; re-exported below)

/**
 * Per-project rollup of orchestrator token amounts plus a theoretical
 * API-cost estimate. Mirrors backend `TokenSummary`. Three independent
 * dimensions are surfaced separately by the UI: amounts (real),
 * theoretical API cost (estimate, must carry the disclaimer), and
 * subscription quota (linked from `/api/cli/quota`, not folded here).
 */
// (Token rollups + ad-hoc usage + timeline now in features/tokens/models/tokens.model.ts; re-exported below)

// (ProjectTokenCategory, ProjectTokenUsageSummary, ProjectTokenHeatmapCell,
// ProjectTokenHeatmapJob, ProjectTokenHeatmap, ProjectExpensiveJob,
// ProjectExpensiveJobsResponse, ProjectJobTokenRun, ProjectJobTokenDetail
// now in features/project-token-usage/models; re-exported below.)

/**
 * Mirrors backend `TaskScreenshot`. One screenshot file produced during
 * a job's runs and harvested into `<job>/results/`. The strip in the
 * protocol pane and the workspace visual evidence reel both render
 * arrays of these.
 */
// (TaskScreenshot + TaskScreenshotsResponse + WorkspaceScreenshotsResponse now in features/screenshots/models; re-exported below)

/**
 * Long-lived orchestrator session record. The orchestrator boots one of
 * these per project at app start; subsequent decisions resume the same
 * Claude session via `-r <sessionId>`, so the orchestrator carries
 * project context and prior decisions in its conversation memory.
 * Mirrors backend `OrchestratorSession`.
 */
// (Orchestrator session + chat now in features/orchestrator/models; re-exported below)

/**
 * One turn returned by the Slice D project-chat surface
 * (`/api/projects/{project}/chat/...`). Wider author + kind enums
 * than the legacy `OrchestratorChatTurn`: the new tree carries
 * embedded events (tool-call / watchdog / rate-limit / ...) as
 * first-class records alongside conventional turns.
 */
// (Project chat turn + responses now in features/project-chat/models; re-exported below)

// (TokenSummaryByModel now in features/tokens/models/tokens.model.ts; re-exported below)

export interface TaskSummaryState {
  status: TaskSummaryStatus;
  startedAt: string | null;
  finishedAt: string | null;
  errorMessage: string | null;
  bytesWritten: number | null;
}

/**
 * One entry in the prompt-extension timeline. Backend writes prompt-1.md,
 * prompt-2.md, ... when the user sends a follow-up in Extend mode. The
 * Task Description pane renders these as a blog-style sequence below the
 * original task body.
 */
export interface TaskPromptHistoryEntry {
  index: number;
  fileName: string;
  markdown: string;
  writtenAt: string;
}

/**
 * One entry in the task's title-revision timeline, stored in
 * `title-history.json` in the job folder. Appended by the rename
 * endpoint whenever the title actually changes. Oldest first.
 */
export interface TaskTitleHistoryEntry {
  at: string;
  oldTitle: string;
  newTitle: string;
  source: string;
}

export interface TaskDetail {
  info: TaskInfo;
  promptMarkdown: string | null;
  promptHistory: TaskPromptHistoryEntry[];
  titleHistory: TaskTitleHistoryEntry[];
  statusMarkdown: string | null;
  statusGeneration?: FileGenerationMeta | null;
  contextUsage: ContextUsageSnapshot | null;
  log: TaskLogEntry[];
  summaryState: TaskSummaryState | null;
  /**
   * Task-level review evidence. Populated from
   * `<job>/results/review-evidence.jsonl`. Findings produced by security
   * audits, code-review passes, task checks, or human notes. Empty when
   * the file is absent. Findings are evidence for review, not blockers:
   * the lane transitions never gate on them. See
   * `docs/contracts/filesystem.md` "results/review-evidence.jsonl".
   */
  reviewEvidence: ReviewEvidenceEntry[];
}

export type ReviewEvidenceSource = 'security-audit' | 'code-review' | 'task-check' | 'human-note' | 'other';
export type ReviewEvidenceSeverity = 'info' | 'warn' | 'high';

export interface ReviewEvidenceEntry {
  id: string;
  source: ReviewEvidenceSource;
  severity: ReviewEvidenceSeverity;
  title: string;
  body: string | null;
  createdAt: string;
  runIndex: number | null;
  artifacts: string[];
  fileRefs: string[];
  acknowledged: boolean;
  followupJobId: string | null;
}

export interface ContextUsageSnapshot {
  at: string;
  command: string;
  status: string;
  error: string | null;
  metrics: ContextUsageMetric[];
  sections: ContextUsageSection[];
  notes: string[];
  rawText: string;
}

export interface ContextUsageMetric {
  label: string;
  value: string;
}

export interface ContextUsageSection {
  title: string;
  items: string[];
}

export interface TaskLogEntry {
  timestamp: string;
  event: string;
  detail: string | null;
}

/**
 * Kind classification used by the Files tab (F48). Mirrors
 * `TaskArtifactKind` on the backend; values arrive as camel-case strings
 * because of `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.
 */
export type TaskArtifactKind = 'prompt' | 'aspect' | 'codeReview' | 'note' | 'other';

export interface FileGenerationMeta {
  file: string;
  kind: string;
  model?: string | null;
  cli?: string | null;
  tokensIn: number;
  tokensOut: number;
  tokensTotal: number;
  startedAt?: string | null;
  endedAt?: string | null;
  durationMs: number;
  runIndex?: number | null;
  stepId?: string | null;
  headShaAfter?: string | null;
}

/**
 * One `.md` file in the job root surfaced by the Files tab. The content
 * itself is not embedded — the Files tab fetches it lazily through
 * `GET /api/tasks/{id}/files/{fileName}` only when the user expands the
 * card (or when it's the sole prompt and auto-expanded).
 */
export interface TaskArtifact {
  name: string;
  sizeBytes: number;
  mtime: string;
  kind: TaskArtifactKind;
  /** Set when `kind === 'aspect'`; e.g. `code-quality` for `aspect-code-quality.md`. */
  aspectName?: string | null;
  generation?: FileGenerationMeta | null;
}

export interface TaskArtifactsResponse {
  jobId: string;
  files: TaskArtifact[];
}

export type TaskFileSourceScope = 'auto' | 'workspace' | 'code';

export interface TaskFileVersionProvenance {
  source: TaskFileSourceScope | string;
  path: string;
  steps?: string | null;
  generation?: FileGenerationMeta | null;
}

export interface TaskFileHistoryEntry {
  sha: string;
  at?: string | null;
  runIndex?: number | null;
  verdict?: string | null;
  message: string;
  author: string;
  provenance: TaskFileVersionProvenance;
}

export interface GroupedJobs {
  /** Triage staging area; default landing for new jobs, never auto-picked. */
  backlog: TaskInfo[];
  preparation: TaskInfo[];
  /**
   * Retired 1a-orchestrator-prep lane. Orchestrator prep now runs in-place on
   * 1-preparation as the optional `pre-orchestrator-prep` pipeline step (see
   * PipelineCatalogue); the board no longer renders this lane. The field stays
   * so the frontend keeps parsing the grouped payload (same retired-lane
   * pattern as `failedPickup`), and the backend boot-migrates any stray 1a
   * cards back to 1-preparation. Always empty after a clean boot.
   */
  orchestratorPrep: TaskInfo[];
  ready: TaskInfo[];
  progress: TaskInfo[];
  /**
   * ADR-0051 drain-era plumbing: the retired 3a-failed-pickup lane. No live
   * path populates it and the board no longer renders it; the field stays so
   * the frontend keeps parsing the grouped payload while the backend boot
   * drain empties any historical folders. Always empty after a clean boot.
   */
  failedPickup: TaskInfo[];
  /**
   * Park lane for tasks that exhausted their auto-pickup retry budget without
   * reaching review (3b-code-not-complete). Hide-when-empty. The runner moves
   * an offender here and keeps auto-mode running; the project only flips to
   * manual once the systemic "3x3" pattern trips.
   */
  codeNotComplete: TaskInfo[];
  /** ADR-0025 lane: orchestrator's review pass (4-auto-review). */
  autoReview: TaskInfo[];
  /** ADR-0025 lane: waiting for the user (5-human-review). */
  humanReview: TaskInfo[];
  /** Decision lane: true escalations that need operator input (5e-escalated). */
  escalated: TaskInfo[];
  /** Legacy alias for pre-ADR-0025 clients; equal to `autoReview`. */
  review: TaskInfo[];
  completed: TaskInfo[];
  archive: TaskInfo[];
}

/**
 * ASS-1727 — one row in the paged Archive read endpoint
 * (`GET /api/tasks/archive`). The board `grouped.archive` lane is
 * intentionally empty (the cache-backed board scan excludes the terminal
 * lane), so the Archive view hydrates from this slim projection instead of
 * the full `TaskInfo`. Mirrors backend `ArchivedTaskInfo`.
 */
export interface ArchivedTaskInfo {
  id: string;
  taskKey: string;
  key?: string | null;
  title: string;
  state: string;
  projectName: string;
  watchPath: string;
  enteredLaneAt: string;
  lastActivity: string;
  commitCount: number;
  codeActivityDetected: boolean;
  taskType: string;
  cliType?: string | null;
  agent: string;
}

/**
 * ASS-1727 — paged envelope for `GET /api/tasks/archive`. `total` is the
 * full unpaged count (drives "load more" / empty-state); `items` is the
 * newest-first slice for the requested `offset`/`limit`. Mirrors backend
 * `ArchivedTasksResponse`.
 */
export interface ArchivedTasksResponse {
  items: ArchivedTaskInfo[];
  total: number;
  offset: number;
  limit: number;
}

export interface CreateTaskRequest {
  id?: string;
  title: string;
  order?: number;
  agent: string;
  watchPath: string;
  promptMarkdown?: string;
  targetState?: string;
  cliType?: CliType;
  model?: string;
  thinkingLevel?: string;
  /** One of `bug`, `feature`, `chore`. Defaults to `chore` server-side. */
  taskType?: string;
  /** Workspace tag ids to attach on create. */
  tags?: string[];
  /** Card kind: `task` (default) or `epic`. */
  kind?: TaskKind;
  /** Parent epic id (assignment way 1: created as a sub-task of this epic). */
  epicId?: string;
  /** Execution mode. Defaults to `coding` server-side. */
  mode?: TaskMode;
  /** Web access. When omitted, defaults by mode (research = on, else off). */
  allowWebAccess?: boolean;
}

/**
 * Payload from GET /api/tasks/{id}/promote-to-coding: a pre-filled coding-task
 * draft derived from a finished planning task. The frontend seeds the existing
 * create-task modal with these fields and re-uploads `attachments` byte-for-byte
 * into the new task. See docs/research/planning-research-task-kinds-2026-05.md.
 */
export interface PromoteToCodingResponse {
  title: string;
  promptMarkdown: string;
  mode: TaskMode;
  targetState: string;
  watchPath: string;
  projectName: string;
  attachments: PromoteAttachmentRef[];
}

/** One copyable image attachment from a promoted planning task. */
export interface PromoteAttachmentRef {
  fileName: string;
  /** Source folder on the planning task: `results` or `attachments`. */
  source: string;
  /** Relative API URL serving the image bytes. */
  url: string;
}

/**
 * One epic + its live sub-task rollup, from GET /api/epics. Progress is derived
 * from the sub-tasks' lanes server-side, so it always matches the board.
 */
export interface EpicRollup {
  id: string;
  /** Stable human key of the epic (e.g. "ASS-597"); null on epics minted before keys existed. */
  key?: string | null;
  title: string;
  projectName: string;
  watchPath: string;
  state: string;
  subTaskTotal: number;
  completed: number;
  inProgress: number;
  open: number;
  byState: Record<string, number>;
  subTasks: EpicSubTaskRef[];
}

export interface EpicSubTaskRef {
  id: string;
  title: string;
  state: string;
  order: number;
  orchestratorVerdict?: 'pending' | 'reissue' | 'escalate' | 'accept' | null;
}

/** Body for POST /api/epics/{id}/sub-tasks (assignment way 3, deterministic half). */
export interface CreateEpicSubTasksRequest {
  subTasks: EpicSubTaskSpec[];
}

export interface EpicSubTaskSpec {
  title: string;
  promptMarkdown?: string;
  cliType?: CliType;
  model?: string;
  thinkingLevel?: string;
}

/**
 * Workspace-level tag registry entry served by GET /api/tags. Drives the
 * label + colour for tag chips on cards and in the filter bar.
 */
export interface TagRegistryEntry {
  id: string;
  label: string;
  color: string;
  description: string;
}

export interface TaskOrderItem {
  jobId: string;
  watchPath: string;
}

export interface WatchPathEntry {
  name: string;
  path: string;
  rootPath: string;
}

/**
 * F45a / ADR-0042 — flat project summary returned by `GET /api/projects`
 * and embedded under `WorkspaceListItem.projects`. Mirrors backend
 * `ProjectSummary`.
 */
export interface RegistryProjectSummary {
  id: string;
  displayName: string;
  shortCode: string;
  workspaceId: string;
  color: string | null;
  cliDefault: string | null;
  modelDefault: string | null;
  sortOrder: number;
  storageLocation: string;
  archived: boolean;
  createdAt: string;
}

export interface CreateRegistryProjectRequest {
  workspaceId: string;
  displayName: string;
  shortCode?: string;
  cliDefault?: CliType;
  modelDefault?: string | null;
  color?: string | null;
}

/**
 * F45a / ADR-0042 — workspace listing entry returned by `GET /api/workspaces`.
 * Mirrors backend `WorkspaceListItem`; embeds the active (non-archived)
 * projects so the sidebar can render in a single round-trip.
 */
export interface RegistryWorkspaceListItem {
  id: string;
  displayName: string;
  sortOrder: number;
  isDefault: boolean;
  color: string | null;
  createdAt: string;
  projects: RegistryProjectSummary[];
}

export interface CliExecution {
  jobId: string;
  taskKey: string;
  processId: number;
  startedAt: string;
  status: string;
  exitCode: number | null;
  durationSeconds: number | null;
  model: string | null;
  thinkingLevel?: string | null;
  runOutcome?: string | null;
}

/**
 * ASS-1751: the four ways a `3-progress` card can look "untouched", as
 * classified by the backend at read time:
 * - `active` — a run process is alive and occupies a parallelism slot.
 * - `failed-backoff` — the last run failed and a rapid-crash backoff is still
 *   in effect; the task is waiting for re-pickup (carries `backoffUntil`).
 * - `failed-idle` — the last run failed (or a fail-without-progress streak is
 *   recorded) but no backoff is active and nothing is running.
 * - `no-active-run` — no live run, no backoff, no recorded failure; e.g. an
 *   orphan after a backend restart awaiting re-pickup.
 */
export type TaskRunActivityKind = 'active' | 'failed-backoff' | 'failed-idle' | 'no-active-run';

/**
 * Read-time visibility projection for a `3-progress` task (ASS-1751). Purely
 * informational — it carries no behavior; the kanban card and the task-detail
 * header render a small, quiet status pill from it. Present only on
 * Progress-lane tasks; absent (null/undefined) on every other lane.
 */
export interface TaskRunActivity {
  kind: TaskRunActivityKind;
  /** OS process id of the live run; set only when `kind === 'active'`. */
  processId?: number | null;
  /** UTC ISO instant the rapid-crash backoff expires; set only when `kind === 'failed-backoff'`. */
  backoffUntil?: string | null;
  /** Consecutive fail-without-progress attempts recorded for this task (0 when none). */
  attempt: number;
  /** One-line last-error summary mirrored from the outcome issue; null when unknown. */
  lastError?: string | null;
}

export interface CliOutputLine {
  timestamp: string;
  stream: string;
  text: string;
}

export interface ProjectRunnerStatus {
  projectName: string;
  mode: string;
  activeJobId: string | null;
  activeExecution: CliExecution | null;
  queuedJobIds: string[];
  /**
   * Human-readable reason recorded the last time the runner mode changed
   * (e.g. `auto-failure circuit-breaker: 3x same job 'foo' did not reach
   * review`, `api-toggle`, `supervisor pause: ...`). Lets the lane chip
   * distinguish operator-initiated `manual` / `paused` transitions from
   * system-initiated ones. Null on legacy in-memory records.
   */
  modeReason?: string | null;
  /** UTC timestamp when the mode last changed. Null when not recorded. */
  modeChangedAt?: string | null;
  /**
   * Coarse classification of where the current mode came from. One of
   * `user`, `circuit-breaker`, `supervisor`, `system`. Computed on the
   * backend from `modeReason` so the frontend does not have to re-implement
   * the heuristic on every render.
   */
  modeSource?: 'user' | 'circuit-breaker' | 'supervisor' | 'system' | string | null;
  /** Current global auto-failure breaker state; `cooldown` means auto-resume is scheduled. */
  breakerState?: 'cooldown' | string | null;
  /** UTC instant when the global breaker cooldown expires. */
  breakerCooldownUntil?: string | null;
  /** Human-readable reason for the active global breaker cooldown. */
  breakerReason?: string | null;
  /** Number of global breaker trips since backend startup. */
  breakerTripCount?: number;
  /**
   * Backend role assigned via `Runner:Role` config (ADR-0044). `orchestrator`
   * runs the auto-pickup loop (stable seat); `test-subject` structurally
   * disables auto-pickup so the dev backend can be observed by Playwright
   * specs without racing stable on the shared workspace. Defaults to
   * `orchestrator` when older backends return without the field.
   */
  role?: 'orchestrator' | 'test-subject' | string | null;
  /**
   * Mode the operator asked for while a job was still running. Non-null only
   * when a `PUT /api/runner/{project}/mode` with `manual` / `paused` arrived
   * while {@link activeJobId} was set. The runner applies the value the
   * moment the active job clears; the lane pill renders as
   * "MANUAL (after current)" while this is populated. See ADR-0044.
   */
  pendingMode?: string | null;
  /** Job id the deferred mode change is waiting on. */
  pendingModeWillApplyAfter?: string | null;
}

/**
 * Response body for `PUT /api/runner/{project}/mode` (ADR-0044).
 * `applied: true` means the live mode moved immediately; `applied: false`
 * means the change is queued behind the active job, in which case
 * {@link pendingMode} + {@link willApplyAfterJobId} carry the deferred
 * value. The frontend renders the lane pill as
 * "{mode} (then {pendingMode} after {willApplyAfterJobId})" while the
 * deferred change is pending.
 */
export interface SetRunnerModeResponse {
  applied: boolean;
  mode: string;
  pendingMode?: string | null;
  willApplyAfterJobId?: string | null;
}

export interface RunnerStatus {
  projects: Record<string, ProjectRunnerStatus>;
}

/**
 * Cycle 5 single-round-trip snapshot for the project-detail panel.
 * Returned by GET /api/projects/{projectName}/snapshot. Matches the
 * anonymous-object shape that ProjectSnapshotEndpoints emits; the
 * standalone endpoints (settings, runner-status, orchestrator-log,
 * orchestrator-session, review-decisions-pending, runner-pending-decisions)
 * remain available so other consumers don't churn.
 */
export interface ProjectSnapshot {
  project: string;
  capturedAt: string;
  paths: {
    path: string;
    rootPath: string | null;
    repositoryPath: string | null;
  };
  settings: {
    autoCommit: boolean;
    crashRecoveryEnabled: boolean;
    autoPushStrategy: 'never' | 'on-completed' | 'always-immediate';
    runnerMode: string | null;
    orchestratorModel: string | null;
    /** F35: every lane resolved to its effective sort strategy (defaults filled in). */
    laneSortStrategies?: Record<string, string>;
  };
  runnerStatus: ProjectRunnerStatus | null;
  orchestratorLogTail: OrchestratorLogEntry[];
  orchestratorSession: OrchestratorSession | null;
  reviewDecisionsPending: { jobId: string; title: string; reason: string | null }[];
  runnerPendingDecisions: { jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }[];
  queueHealth: ProjectQueueHealth;
}

export interface CrashRecoveryPending {
  id: string;
  createdAt: string;
  projectName: string;
  jobId: string | null;
  repoRoot: string;
  files: string[];
  message: string;
  reason: string;
}

export interface CrashRecoveryActionResult {
  status: 'committed' | 'dismissed' | 'failed' | 'not-found' | 'nothing-to-commit' | string;
  pending: CrashRecoveryPending | null;
  commitSha: string | null;
  error: string | null;
}

export interface ProjectQueueHealthLocation {
  id: string;
  lane: string;
  hasJobJson: boolean;
  path: string;
}

export interface ProjectQueueHealthDuplicate {
  id: string;
  locations: ProjectQueueHealthLocation[];
}

export interface ProjectQueueHealthStateMismatch {
  id: string;
  lane: string;
  state: string | null;
  path: string;
}

export interface ProjectQueueHealth {
  severity: 'ok' | 'warning' | 'critical' | string;
  issueCount: number;
  missingJobJson: ProjectQueueHealthLocation[];
  duplicates: ProjectQueueHealthDuplicate[];
  stateMismatches: ProjectQueueHealthStateMismatch[];
}

export interface CliSettings {
  path: string;
  available: boolean;
  version: string | null;
  hasToken: boolean;
}

// ── End of shared kernel ──
//
// Cycle 9i: Feature-specific types (quota, cli catalog/usage,
// project-token-usage, etc.) are no longer re-exported from this file.
// Import them from the relevant feature barrel instead, e.g.:
//
//   import type { QuotaReport } from '../features/quota';
//   import type { CliUsageReport } from '../features/cli';
//   import type { ProjectTokenHeatmap } from '../features/project-token-usage';
//
// See frontend/AGENTS.md "Feature folders + barrel imports" for the
// rule and rationale.
