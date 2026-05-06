export type CliType = 'copilot' | 'claude' | 'codex' | 'gemini';
export const CLI_TYPES: CliType[] = ['copilot', 'claude', 'codex', 'gemini'];

export interface GitFileChange {
  status: string;
  path: string;
  added: number;
  removed: number;
}

export interface GitStatus {
  isRepo: boolean;
  branch: string | null;
  filesChanged: number;
  totalAdded: number;
  totalRemoved: number;
  files: GitFileChange[];
  error: string | null;
}

export interface ClaudeSessionInfo {
  sessionId: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  totalTokens: number;
  lastTurnAt: string | null;
  turnCount: number;
  error: string | null;
}

export interface ClaudeRateLimitSnapshot {
  window: string | null;          // e.g. "five_hour", "weekly"
  status: string | null;          // e.g. "allowed", "exceeded"
  resetsAt: number;               // Unix epoch (seconds)
  overageStatus: string | null;
  isUsingOverage: boolean;
  capturedAt: string;             // ISO timestamp
}

export interface ClaudeSessionResponse {
  sessionInfo: ClaudeSessionInfo;
  rateLimit: ClaudeRateLimitSnapshot | null;
}

/** One row in `logs/session-events.jsonl` for a job. */
export interface SessionEvent {
  ts: string;                       // ISO timestamp
  kind: 'start' | 'continue' | 'recovery';
  cli: string | null;
  inputSessionId: string | null;
  capturedSessionId: string | null;
  resumed: boolean;
  reason: string | null;
}

export interface SessionEventsResponse {
  events: SessionEvent[];
  /** Ordered list of CLI session ids; the literal string `(recovery)` marks a chain break. */
  sessionChain: string[];
  currentSessionId: string | null;
}

/**
 * One CLI invocation between two user inputs - the unit of conversation
 * the protocol-pane run timeline renders. Backed by RunRecord on the
 * backend (see backend/Services/Runner/RunTimeline.cs). lineStart /
 * lineEnd are 1-based indices into cli-output.log so the drill-down
 * activity-log filter does not have to re-derive the boundaries.
 */
export interface RunRecord {
  index: number;
  intent: string; // 'start' | 'continue' | 'recovery' | 'restart'
  startedAt: string;
  endedAt: string | null;
  status: string; // 'running' | 'completed' | 'failed' | 'cancelled' | 'unknown'
  cli: string | null;
  exitCode: number | null;
  durationSeconds: number | null;
  inputSessionId: string | null;
  capturedSessionId: string | null;
  resumed: boolean;
  reason: string | null;
  userFollowup: string | null;
  lineStart: number | null;
  lineEnd: number | null;
  /** HEAD SHA captured immediately before the run's CLI started, or null when the project has no repo / git was unavailable. */
  headShaBefore: string | null;
  /** HEAD SHA after the run finished. Equal to headShaBefore when the agent did not commit. */
  headShaAfter: string | null;
}

export interface RunTimeline {
  runCount: number;
  firstStartedAt: string | null;
  lastActivityAt: string | null;
  hasActiveRun: boolean;
  runs: RunRecord[];
}

export interface RunCommitInfo {
  sha: string;
  shortSha: string;
  authorDateUtc: string;
  author: string;
  subject: string;
  filesChanged: number;
  added: number;
  removed: number;
}

export interface RunCommitsResponse {
  runIndex: number;
  startedAt: string;
  endedAt: string | null;
  headShaBefore: string | null;
  headShaAfter: string | null;
  /** 'sha-range' (deterministic) | 'wall-clock' (fallback for older runs without captured SHAs). */
  source: 'sha-range' | 'wall-clock';
  commits: RunCommitInfo[];
}

/**
 * One row in the per-run aggregated file list. `status` is the
 * single-letter git diff filter (A/M/D/R/C). The +/- counts are the
 * combined numstat across every commit in the run that touched this
 * path. Used by the Run Git Viewer's file tree.
 */
export interface RunFileChange {
  status: string;
  path: string;
  added: number;
  removed: number;
}

export interface RunFilesResponse {
  runIndex: number;
  headShaBefore: string | null;
  headShaAfter: string | null;
  files: RunFileChange[];
  note?: string;
}

export interface RunDiffResponse {
  diff: string;
  note?: string;
}

export interface GitProjectSummary {
  projectName: string;
  rootPath: string;
  isRepo: boolean;
  branch: string | null;
  filesChanged: number;
  totalAdded: number;
  totalRemoved: number;
}

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
export interface JobTokenSummary {
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  totalTokens: number;
  lastModel: string | null;
  lastUpdate: string | null;
  entries: JobTokenCall[];
}

export interface JobTokenCall {
  ts: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

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

export interface JobCommitInfo {
  sha: string;
  shortSha: string;
  message: string;
  filesChanged: number;
  files: string[];
  at: string;
}

export interface JobCommitDetail {
  commit: JobCommitInfo | null;
  files: GitFileChange[];
}

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
export interface OrchestratorLogEntry {
  ts: string;
  kind: 'decision' | 'action' | 'observation' | 'intervention';
  topic: string;
  summary: string;
  reasoning?: string | null;
  jobId?: string | null;
  tokenUsage?: OrchestratorTokenUsage | null;
  userOverride?: { at: string; newDirection: string } | null;
}

export interface OrchestratorTokenUsage {
  model?: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

export interface OrchestratorLogResponse {
  project: string;
  entries: OrchestratorLogEntry[];
}

/**
 * Per-project rollup of orchestrator token amounts plus a theoretical
 * API-cost estimate. Mirrors backend `TokenSummary`. Three independent
 * dimensions are surfaced separately by the UI: amounts (real),
 * theoretical API cost (estimate, must carry the disclaimer), and
 * subscription quota (linked from `/api/cli/quota`, not folded here).
 */
export interface TokenSummary {
  project: string;
  orchestratorEntries: number;
  orchestratorLlmCalls: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheReadTokens: number;
  totalCacheCreationTokens: number;
  estimatedApiCostUsd: number;
  allModelsPriced: boolean;
  byModel: TokenSummaryByModel[];
  disclaimer: string;
}

/**
 * Workspace-wide rollup of orchestrator tokens + theoretical API cost.
 * Mirrors backend `TokenSummaryAggregate`. Used by the status-bar usage
 * modal to render a single "tokens consumed" number on hover, without
 * forcing the user to pick a project first.
 */
export interface TokenSummaryAggregate {
  projects: number;
  orchestratorEntries: number;
  orchestratorLlmCalls: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheReadTokens: number;
  totalCacheCreationTokens: number;
  estimatedApiCostUsd: number;
  allModelsPriced: boolean;
  byModel: TokenSummaryByModel[];
  byProject: TokenSummaryByProject[];
  fetchedAt: string;
  disclaimer: string;
}

export interface TokenSummaryByProject {
  project: string;
  orchestratorLlmCalls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
}

/**
 * Workspace token-usage timeline. Mirrors backend `TokenTimeline`.
 * Powers the workspace token view at `#/workspace/tokens`. Each cell is
 * a (project, time-bucket) datum that the chart stacks on the y axis.
 */
export interface TokenTimeline {
  windowStart: string;
  windowEnd: string;
  windowHours: number;
  bucketMinutes: number;
  bucketCount: number;
  cells: TokenTimelineCell[];
  projects: TokenTimelineProject[];
  fetchedAt: string;
  disclaimer: string;
}

export interface TokenTimelineCell {
  project: string;
  bucketStart: string;
  bucketEnd: string;
  calls: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  total: number;
  dollars: number | null;
  allModelsPriced: boolean;
}

export interface TokenTimelineProject {
  project: string;
  calls: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  total: number;
  dollars: number | null;
  allModelsPriced: boolean;
  peakBucketStart: string | null;
  peakBucketTotal: number;
  lastActivity: string | null;
}

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
export interface JobScreenshot {
  jobId: string;
  jobTitle: string;
  projectName: string;
  watchPath: string;
  fileName: string;
  /** Always begins with `results/`. */
  relativePath: string;
  /** Routable URL that serves this file (sub-path aware). */
  url: string;
  caption: string;
  /** `passed` | `failed` | `skipped` | `unknown` | null. */
  status: string | null;
  localPath: string;
  timestampUtc: string;
}

export interface JobScreenshotsResponse {
  jobId: string;
  screenshots: JobScreenshot[];
}

export interface WorkspaceScreenshotsResponse {
  windowHours: number;
  projectFilter: string | null;
  screenshots: JobScreenshot[];
}

/**
 * Long-lived orchestrator session record. The orchestrator boots one of
 * these per project at app start; subsequent decisions resume the same
 * Claude session via `-r <sessionId>`, so the orchestrator carries
 * project context and prior decisions in its conversation memory.
 * Mirrors backend `OrchestratorSession`.
 */
export interface OrchestratorSession {
  sessionId: string;
  model: string;
  bootedAt: string;
  bootPromptPreview: string;
  bootReplyPreview: string;
  cumulativeInputTokens: number;
  cumulativeOutputTokens: number;
  cumulativeCacheReadTokens: number;
  cumulativeCacheCreationTokens: number;
  calls: number;
  lastUsedAt: string;
  lastError?: string | null;
}

export interface OrchestratorSessionResponse {
  project: string;
  session: OrchestratorSession | null;
}

/**
 * One turn in the per-project orchestrator chat. Mirrors backend
 * `OrchestratorChatTurn`. Roles: 'user' for the human's messages,
 * 'orchestrator' for the model's replies. `errorMessage` is set on a
 * failed orchestrator turn so the UI can surface what went wrong without
 * losing the user's text.
 */
export interface OrchestratorChatTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
  model?: string | null;
  tokenUsage?: OrchestratorTokenUsage | null;
  errorMessage?: string | null;
  attachments?: OrchestratorChatAttachment[] | null;
}

export interface OrchestratorChatAttachment {
  alt: string;
  relativePath: string;
}

export interface OrchestratorChatResponse {
  project: string;
  turns: OrchestratorChatTurn[];
}

/**
 * One candidate task produced by the roadmap-intake splitter. The user
 * reviews and edits these in place before confirming; the confirm step
 * materialises them as job folders in <c>1-preparation</c>.
 */
export interface RoadmapIntakeCandidate {
  title: string;
  promptBody: string;
  kind: 'feature' | 'bug' | 'adr' | 'chore' | 'research' | string;
  suggestedOrder: number;
  suggestedCliType: string;
  rationale: string;
}

export interface RoadmapIntakeResponse {
  candidates: RoadmapIntakeCandidate[];
  notes: string;
}

export interface RoadmapIntakeConfirmResponse {
  created: { jobId: string; title: string; state: string }[];
  skipped: string[];
}

export interface TokenSummaryByModel {
  model: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
}

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

export interface QuotaWindow {
  label: string;
  usedPct: number | null;
  used: number | null;
  limit: number | null;
  unit: string | null;
  resetAt: string | null;
  resetLabel: string | null;
}

export interface QuotaSnapshot {
  cliType: CliType;
  fetchedAt: string;
  plan: string | null;
  windows: QuotaWindow[];
  source: string | null;
  rawSample: string | null;
  error: string | null;
}

export interface QuotaReport {
  at: string;
  /**
   * Cache TTL in seconds. The UI computes the "stale" badge as
   * `now - snapshot.fetchedAt > ttlSeconds`. When the field is missing
   * (older backends), treat as 600.
   */
  ttlSeconds?: number;
  snapshots: QuotaSnapshot[];
}
