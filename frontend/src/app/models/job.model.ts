export type CliType = 'copilot' | 'claude' | 'codex' | 'gemini';
export const CLI_TYPES: CliType[] = ['copilot', 'claude', 'codex', 'gemini'];

// Cycle 9: git types live under features/git/models/git.model.ts.
// Re-exported here so existing imports keep working; new code should
// import from the feature folder directly.
export type {
  GitFileChange, GitStatus, GitProjectSummary, GitHygieneStatus,
  JobHygieneContext, JobCommitInfo, JobCommitDetail
} from '../features/git/models/git.model';
// Internal aliases: re-export above is for external consumers; this
// import lets the still-in-this-file types (e.g. JobInfo) reference
// JobCommitInfo without the alias prefix.
import type { JobCommitInfo, GitFileChange } from '../features/git/models/git.model';

// Cycle 9: Claude session types live under
// features/claude/models/claude-session.model.ts. Re-exported here
// so existing imports keep working.
export type {
  ClaudeSessionInfo, ClaudeRateLimitSnapshot, ClaudeSessionResponse
} from '../features/claude/models/claude-session.model';

// Cycle 9: token rollups + ad-hoc usage + timeline live under
// features/tokens/models/tokens.model.ts.
export type {
  JobTokenSummary, JobTokenCall, TokenSummary, TokenSummaryAggregate,
  TokenSummaryByProject, TokenSummaryByModel, TokenTimeline,
  TokenTimelineCell, TokenTimelineProject, AdHocUsageAggregate,
  AdHocUsageBySource, AdHocUsageByDay, AdHocUsageByModel
} from '../features/tokens/models/tokens.model';
import type { JobTokenSummary } from '../features/tokens/models/tokens.model';

// Cycle 9: per-job run timeline (between user inputs).
export type {
  RunRecord, RunTimeline, RunCommitInfo, RunCommitsResponse,
  RunFileChange, RunFilesResponse, RunDiffResponse
} from '../features/run-timeline/models/run-timeline.model';

// Cycle 9: orchestrator log + session + chat (manager-style
// conversation alongside the agent runs).
export type {
  OrchestratorLogEntry, OrchestratorTokenUsage, OrchestratorLogResponse,
  OrchestratorSession, OrchestratorSessionResponse,
  OrchestratorChatTurn, OrchestratorChatAttachment, OrchestratorChatResponse
} from '../features/orchestrator/models/orchestrator.model';
import type { OrchestratorLogEntry, OrchestratorSession } from '../features/orchestrator/models/orchestrator.model';

// Cycle 9: per-job session-event log rows.
export type { SessionEvent, SessionEventsResponse } from '../features/session-events/models/session-events.model';

// Cycle 9: per-job + workspace screenshot listings.
export type { JobScreenshot, JobScreenshotsResponse, WorkspaceScreenshotsResponse } from '../features/screenshots/models/screenshots.model';

// Cycle 9: project-chat (Slice D) turns + responses + search hits.
export type { ProjectChatTurn, ProjectChatScrollResponse, ProjectChatSearchHit, ProjectChatSearchResponse, ProjectChatTurnResponse } from '../features/project-chat/models/project-chat.model';

// Cycle 9: roadmap-intake splitter candidates + responses.
export type { RoadmapIntakeCandidate, RoadmapIntakeResponse, RoadmapIntakeConfirmResponse } from '../features/roadmap/models/roadmap.model';

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

// (GitProjectSummary, GitHygieneStatus, JobHygieneContext now in features/git/models/git.model.ts; re-exported above)

export interface JobInfo {
  id: string;
  jobKey: string;
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
  tokenSummary?: JobTokenSummary | null;
  model: string | null;
  cliType: CliType | null;
  useOwnSession: boolean | null;
  lastUsage: SessionUsage | null;
  execution: CliExecution | null;
  commit: JobCommitInfo | null;
  /**
   * Ordered chain of commits attributed to this task (oldest -> newest).
   * Tasks regularly produce more than one commit across iterations
   * (continue-mode follow-up, crash-recovery + repair, operator-driven
   * steers). Backwards compat: when only the legacy singular `commit`
   * is on disk, the backend surfaces it here as `[commit]`.
   */
  commits?: JobCommitInfo[];
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
  summaryState?: JobSummaryState | null;
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
   * Optional lifecycle substate. Mirrors backend `JobInfo.Phase`. Drives
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
}

/**
 * Per-job orchestrator token rollup. Mirrors backend `JobTokenSummary`.
 * Surfaced on the kanban card as a colour-tiered "token bubble" with a
 * hover popover that lists per-call rows.
 */
// (JobTokenSummary, JobTokenCall now in features/tokens/models/tokens.model.ts; re-exported below)

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

// (JobCommitInfo, JobCommitDetail now in features/git/models/git.model.ts; re-exported above)

export interface SessionUsage {
  at: string;
  tokens: string | null;
  changes: string | null;
  requests: string | null;
}

export interface CliModelInfo {
  id: string;
  label: string;
  multiplier: number | null;
  vendor: string | null;
  isDefault: boolean;
}

export interface CliModelCatalog {
  models: CliModelInfo[];
  source: string;
  fetchedAt?: string;
}

// Backwards-compat aliases — the records were Copilot-named before the multi-CLI refactor.
export type CopilotModelInfo = CliModelInfo;
export type CopilotModelCatalog = CliModelCatalog;

export interface CliSessionInfo {
  id: string;
  label: string | null;
  updatedAt: string | null;
  cwd: string | null;
  lastUsage: SessionUsage | null;
  isProjectDefault: boolean;
}

export interface CliUsageProjectGroup {
  projectName: string;
  rootPath: string | null;
  sessions: CliSessionInfo[];
}

export interface CliUsageSection {
  cliType: CliType;
  available: boolean;
  version: string | null;
  error: string | null;
  projects: CliUsageProjectGroup[];
}

export interface CliUsageReport {
  at: string;
  sections: CliUsageSection[];
}

export type JobSummaryStatus = 'none' | 'generating' | 'ready' | 'failed';

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
 * Discriminated response for `POST /api/jobs/{id}/continue` and `/start`.
 * `started` means the run is live; `queued` means the project was busy
 * with another job, the user's intent has been saved on the target task,
 * and the target task is now at the top of `2-ready`. The frontend treats
 * `queued` as success-with-info (no modal); the chat carries the
 * orchestrator's `[queued]` line.
 */
export interface ContinueJobResponse {
  status: 'started' | 'queued';
  execution?: CliExecution | null;
  queued?: ContinueJobQueuedInfo | null;
}

export interface ContinueJobQueuedInfo {
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

/**
 * Project Token Usage models (slice 8 of the quality-system mockup,
 * docs/mockups/quality-system/). Mirrors backend
 * `ProjectTokenUsageSummary`, `ProjectTokenHeatmap`, etc. The category
 * split (`job` / `supporting` / `orchestrator`) follows taxonomy.md.
 */
export type ProjectTokenCategory = 'job' | 'supporting' | 'orchestrator';

export interface ProjectTokenUsageSummary {
  project: string;
  hasData: boolean;
  lifetimeTotalTokens: number;
  lifetimeJobTokens: number;
  lifetimeSupportingTokens: number;
  lifetimeOrchestratorTokens: number;
  lifetimeCalls: number;
  last24hTotalTokens: number;
  last24hJobTokens: number;
  last24hSupportingTokens: number;
  last24hOrchestratorTokens: number;
  last24hCalls: number;
  firstActivity: string | null;
  lastActivity: string | null;
  fetchedAt: string;
  disclaimer: string;
}

export interface ProjectTokenHeatmapCell {
  day: string;
  total: number;
}

export interface ProjectTokenHeatmapJob {
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  total: number;
  calls: number;
  lastActivity: string | null;
  cells: ProjectTokenHeatmapCell[];
}

export interface ProjectTokenHeatmap {
  project: string;
  days: string[];
  jobs: ProjectTokenHeatmapJob[];
  hasData: boolean;
  fetchedAt: string;
}

export interface ProjectExpensiveJob {
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  totalTokens: number;
  calls: number;
  lastActivity: string | null;
  lastModel: string | null;
}

export interface ProjectExpensiveJobsResponse {
  project: string;
  jobs: ProjectExpensiveJob[];
}

export interface ProjectJobTokenRun {
  index: number;
  ts: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  total: number;
  deltaVsPrev: number | null;
  topic: string | null;
  summary: string | null;
}

export interface ProjectJobTokenDetail {
  project: string;
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  totalTokens: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  calls: number;
  firstActivity: string | null;
  lastActivity: string | null;
  lastModel: string | null;
  runs: ProjectJobTokenRun[];
  fetchedAt: string;
}

/**
 * Mirrors backend `JobScreenshot`. One screenshot file produced during
 * a job's runs and harvested into `<job>/results/`. The strip in the
 * protocol pane and the workspace visual evidence reel both render
 * arrays of these.
 */
// (JobScreenshot + JobScreenshotsResponse + WorkspaceScreenshotsResponse now in features/screenshots/models; re-exported below)

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

/**
 * One candidate task produced by the roadmap-intake splitter. The user
 * reviews and edits these in place before confirming; the confirm step
 * materialises them as job folders in <c>1-preparation</c>.
 */
// (RoadmapIntake* now in features/roadmap/models; re-exported below)

// (TokenSummaryByModel now in features/tokens/models/tokens.model.ts; re-exported below)

export interface JobSummaryState {
  status: JobSummaryStatus;
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
export interface JobPromptHistoryEntry {
  index: number;
  fileName: string;
  markdown: string;
  writtenAt: string;
}

export interface JobDetail {
  info: JobInfo;
  promptMarkdown: string | null;
  promptHistory: JobPromptHistoryEntry[];
  statusMarkdown: string | null;
  contextUsage: ContextUsageSnapshot | null;
  log: JobLogEntry[];
  summaryState: JobSummaryState | null;
  /**
   * Task-level review evidence. Populated from
   * `<job>/results/review-evidence.jsonl`. Findings produced by security
   * audits, code-review passes, task checks, or human notes. Empty when
   * the file is absent. Findings are evidence for review, not blockers:
   * the lane transitions never gate on them. See
   * `docs/filesystem-contract.md` "results/review-evidence.jsonl".
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

export interface JobLogEntry {
  timestamp: string;
  event: string;
  detail: string | null;
}

export interface GroupedJobs {
  /** Triage staging area; default landing for new jobs, never auto-picked. */
  backlog: JobInfo[];
  preparation: JobInfo[];
  /** ADR-0026 lane: orchestrator-prep (1a-orchestrator-prep). */
  orchestratorPrep: JobInfo[];
  /** ADR-0026 lane: needs-human-review (1b-needs-human-review). Hide-when-empty. */
  needsHumanReview: JobInfo[];
  ready: JobInfo[];
  progress: JobInfo[];
  /**
   * ADR-0028 lane: pickup failures (3a-failed-pickup). Hide-when-empty.
   * Populated by StaleProgressArchiver and the per-project dead-letter path.
   * Renders with the amber loud-not-archived treatment: orphan / empty
   * boot-sweep verdicts and silent-pickup dead-letters used to vanish into
   * 7-archive; they now stay visible here with a per-card placard
   * (`failed-pickup-reason.md`).
   */
  failedPickup: JobInfo[];
  /** ADR-0025 lane: orchestrator's review pass (4-auto-review). */
  autoReview: JobInfo[];
  /** ADR-0025 lane: waiting for the user (5-human-review). */
  humanReview: JobInfo[];
  /** Legacy alias for pre-ADR-0025 clients; equal to `autoReview`. */
  review: JobInfo[];
  completed: JobInfo[];
  archive: JobInfo[];
}

export interface CreateJobRequest {
  id?: string;
  title: string;
  order?: number;
  agent: string;
  watchPath: string;
  promptMarkdown?: string;
  targetState?: string;
  cliType?: CliType;
  model?: string;
  /** One of `bug`, `feature`, `chore`. Defaults to `chore` server-side. */
  taskType?: string;
  /** Workspace tag ids to attach on create. */
  tags?: string[];
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

export interface JobOrderItem {
  jobId: string;
  watchPath: string;
}

export interface WatchPathEntry {
  name: string;
  path: string;
  rootPath: string;
}

export interface CliExecution {
  jobId: string;
  jobKey: string;
  processId: number;
  startedAt: string;
  status: string;
  exitCode: number | null;
  durationSeconds: number | null;
  model: string | null;
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
}

export interface RunnerStatus {
  projects: { [key: string]: ProjectRunnerStatus };
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
  settings: {
    autoCommit: boolean;
    runnerMode: string | null;
    orchestratorModel: string | null;
  };
  runnerStatus: ProjectRunnerStatus | null;
  orchestratorLogTail: OrchestratorLogEntry[];
  orchestratorSession: OrchestratorSession | null;
  reviewDecisionsPending: { jobId: string; title: string; reason: string | null }[];
  runnerPendingDecisions: { jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }[];
}

export interface CliSettings {
  path: string;
  available: boolean;
  version: string | null;
  hasToken: boolean;
}

// ── Subscription quota / rate-limit reporting ──
// Each CLI exposes one or more "windows" (e.g. monthly premium requests for Copilot,
// 5h+weekly buckets for Codex, rate-limit reset for Claude when over-quota).
// usedPct above 100 means the user has overshot the included allotment.

// Cycle 9: quota types live under features/quota/models/quota.model.ts.
// Re-exported here so existing imports keep working; new code should
// import from the feature folder directly so the boundary stays
// visible. The canonical home is the feature folder.
export type { QuotaWindow, QuotaSnapshot, QuotaReport } from '../features/quota/models/quota.model';
