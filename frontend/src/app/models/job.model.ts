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
  totalSizeBytes: number;
  sessionName: string | null;
  model: string | null;
  cliType: CliType | null;
  useOwnSession: boolean | null;
  lastUsage: SessionUsage | null;
  execution: CliExecution | null;
  commit: JobCommitInfo | null;
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
  ready: JobInfo[];
  progress: JobInfo[];
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
  snapshots: QuotaSnapshot[];
}
