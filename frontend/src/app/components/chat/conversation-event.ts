/**
 * Pure data contract for the next-gen chat conversation grammar
 * (`Frontend:NextGenChat`).
 *
 * This module is the projection layer between raw evidence the app already
 * collects (CLI output lines, run timeline, screenshots, commits, token
 * usage, job metadata) and the renderer the chat hosts will consume. Every
 * type here must stay pure TypeScript: no Angular injection, no DOM types,
 * no localStorage reads, no service calls. Hosts pass the evidence in;
 * `ConversationEvent[]` comes out.
 *
 * The kind taxonomy is anchored in
 * `docs/mockups/chat-window-next-gen/activity-log-edge-cases.md` (the v6
 * edge-case taxonomy for tool burst, watchdog, orchestrator decision,
 * needs-input, capture-fail, parser warning, image evidence, token metric,
 * task and run markers, user / agent message) plus the v7 workbench events
 * (`workbench.summary`, `workbench.gitPreview`, `workbench.visualPreview`,
 * `metric.token`, `taskMarker`, `runMarker`, `traceLink`).
 *
 * Every event keeps a back-reference to the raw log range that produced it
 * so the Trace fallback stays one click away. The compact chat is allowed
 * to hide noise; it is not allowed to delete traceability.
 */

/** 1-based, inclusive line range into the source CLI log. */
export interface RawLineRange {
  /** Logical key for the source log (e.g. job id or `cli-output.log`). */
  source: string;
  /** First line index, 1-based, inclusive. */
  start: number;
  /** Last line index, 1-based, inclusive. */
  end: number;
}

export interface TraceLink {
  /** Linked source range — the chat row's "open in Trace" target. */
  range: RawLineRange;
  /** Optional human label for the link ("Open run 4 in Trace"). */
  label?: string;
}

/** Tool families recognised by the projection. */
export type ToolFamily =
  | 'read'
  | 'search'
  | 'command'
  | 'edit'
  | 'task'
  | 'todo'
  | 'other';

export type ToolBurstSamples = Readonly<Record<string, string | undefined>>;

export type ConversationEventSeverity = 'info' | 'warn' | 'error';

export interface ToolOutputHit {
  path: string;
  line: number;
  column?: number;
  text: string;
}

export interface ToolCommandExecution {
  command: string;
  status: 'running' | 'completed' | 'failed' | 'unknown';
  exitCode: number | null;
  output: string;
  outputLineCount: number;
  outputTruncated: boolean;
  hits?: readonly ToolOutputHit[];
}

/** Stable kind list. New kinds must be appended; existing values must not be reused. */
export type ConversationEventKind =
  // Message stream
  | 'message.user'
  | 'message.taskAgent'
  | 'message.orchestrator'
  | 'message.supervisor'
  | 'message.supportingAgent'
  // Activity-log edge cases (see activity-log-edge-cases.md)
  | 'toolBurst'
  | 'supervisor.wait'
  | 'decision.orchestrator'
  | 'agent.needsInput'
  | 'system.captureFail'
  | 'system.parserWarning'
  | 'system.status'
  | 'artifact.image'
  | 'metric.token'
  // V7 workbench events
  | 'workbench.summary'
  | 'workbench.gitPreview'
  | 'workbench.visualPreview'
  | 'workbench.debug'
  | 'taskMarker'
  | 'runMarker'
  | 'traceLink'
  // System / parser edge cases beyond the v6 baseline
  | 'system.schemaDrift'
  // Queued feedback on a closed / under-review task
  // (see feedback-queued-from-chat.md)
  | 'feedback.queued';

interface ConversationEventBase {
  /** Stable id, deterministic per source range so renderers can dedupe. */
  id: string;
  kind: ConversationEventKind;
  /** ISO timestamp from the originating evidence row. */
  timestamp: string;
  /** Optional severity for renderers that style by level. */
  severity?: ConversationEventSeverity;
  /** Run index from the run timeline, when known. */
  runId?: number;
  /** Job id when the host has cross-task context (project side sheet). */
  jobId?: string;
  /** Back-reference to raw log lines. Required so Trace stays one click away. */
  rawRange: RawLineRange;
  /**
   * Hint to the renderer that the event should start collapsed even though
   * its body may be long. Tool bursts and parser warnings default to true.
   */
  collapsedByDefault?: boolean;
}

export interface MessageEvent extends ConversationEventBase {
  kind:
    | 'message.user'
    | 'message.taskAgent'
    | 'message.orchestrator'
    | 'message.supervisor'
    | 'message.supportingAgent';
  /** Plain or markdown text. The renderer decides how to format. */
  body: string;
  /** Display name for the actor (e.g. `Orchestrator`, `Agent`, `You`). */
  actor: string;
  /** Optional target chip ("→ task: foo") used by user steering messages. */
  target?: string;
  /** Attachment paths or URIs the host already resolved. */
  attachments?: readonly string[];
}

export interface ToolBurstEvent extends ConversationEventBase {
  kind: 'toolBurst';
  /** Total number of tool calls across families. */
  count: number;
  /** Per-family counts. Missing families are treated as zero. */
  families: Partial<Record<ToolFamily, number>>;
  /** Number of calls that ended in error. */
  failures: number;
  /** Wall-clock span in milliseconds, from first to last call. */
  durationMs: number;
  /** Files touched (edit / write / delete). */
  files?: readonly string[];
  /** Test commands and final pass/fail status, when detected. */
  tests?: readonly { command: string; status: 'pass' | 'fail' | 'unknown' }[];
  /** Artifact paths produced (screenshots, reports, etc.). */
  artifacts?: readonly string[];
  /** Representative example per family for the collapsed badge. */
  samples?: ToolBurstSamples;
  /** Shell / PowerShell executions with compact output previews. */
  commands?: readonly ToolCommandExecution[];
}

export interface SupervisorWaitEvent extends ConversationEventBase {
  kind: 'supervisor.wait';
  /** `quiet` for ongoing silence, `resumed` after the agent talked again, `killed` on watchdog kill. */
  state: 'quiet' | 'resumed' | 'killed';
  quietSeconds: number;
  /** Last raw output line range before the wait window started. */
  lastOutputRange?: RawLineRange;
  /** Reason the watchdog gave when emitting the line. */
  reason?: string;
}

export interface OrchestratorDecisionEvent extends ConversationEventBase {
  kind: 'decision.orchestrator';
  /** `decision`, `reissue`, `heuristic`, `giveup`. */
  decisionType: string;
  reason: string;
  /** Short evidence snippet (a sentence or two). */
  evidence?: string;
  /** Action the orchestrator took next: `continue`, `reissue`, `escalate`, `complete`. */
  action?: string;
  /** Retry budget remaining for this lane / loop. */
  retryBudget?: { used: number; max: number };
  /** Token usage attributed to the decision call. */
  tokenUsage?: { inputTokens: number; outputTokens: number };
}

export interface AgentNeedsInputEvent extends ConversationEventBase {
  kind: 'agent.needsInput';
  question: string;
  loopIndex: number;
  loopLimit: number;
  /** `auto-orchestrator`, `human`, or null when no answer source resolved yet. */
  answerSource?: string | null;
  /** What happens next: `await-human`, `auto-answer`, `circuit-break`. */
  nextAction?: string;
}

export interface SystemCaptureFailEvent extends ConversationEventBase {
  kind: 'system.captureFail';
  cliType: string;
  sessionName?: string | null;
  /** Fallback action chosen ("rebuild from disk on next follow-up"). */
  fallback?: string;
}

export interface SystemParserWarningEvent extends ConversationEventBase {
  kind: 'system.parserWarning';
  /** What the parser was looking for (sentinel name, schema kind, etc.). */
  expectedKind: string;
  message: string;
  /** Key used to dedupe identical warnings within a single chat. */
  dedupeKey: string;
}

export interface SystemStatusEvent extends ConversationEventBase {
  kind: 'system.status';
  category: string;
  label: string;
  explanation: string;
  nextStep?: string;
}

export interface ArtifactImageEvent extends ConversationEventBase {
  kind: 'artifact.image';
  caption: string;
  /** Scratch path the agent first emitted (Playwright temp, /tmp, etc.). */
  sourcePath: string;
  /** Durable copy under `results/` after the host curated it; null if not copied. */
  durablePath?: string | null;
  /** Tool that produced the image (`playwright`, `screenshot`, `agent`). */
  sourceTool?: string;
  /** Linked task id for cross-task referencing. */
  taskLink?: string;
}

export interface MetricTokenEvent extends ConversationEventBase {
  kind: 'metric.token';
  /** `run`, `task`, `project`, `orchestrator`, `supporting-agent`. */
  scope: string;
  inputTokens: number;
  outputTokens: number;
  reasoningTokens?: number;
  /** Cost in USD when the host has model pricing; otherwise omit. */
  cost?: number;
  /** Window label (`five_hour`, `weekly`, `month-to-date`). */
  window?: string;
}

/**
 * Aggregate snapshot the v7 summary strip renders without re-parsing UI text.
 * The renderer should treat every field as optional: a host can populate the
 * subset it has cheap access to (e.g. tokens may be unknown when the token
 * summary call has not landed yet).
 *
 * The taxonomy here mirrors `docs/mockups/chat-window-next-gen/README.md` v7
 * summary strip - state/run, tokens, commits, changed files, screenshots,
 * retry/parser warnings, duration, latest result.
 */
export interface WorkbenchSummaryAggregate {
  /** Job lane / state ("3-progress", "4-auto-review", ...). */
  state?: string;
  /** Total runs and the latest run's outcome (`completed` / `failed` / ...). */
  runCount?: number;
  latestRunStatus?: string;
  latestRunIntent?: string;
  /** Total wall-clock duration across runs in seconds, when known. */
  totalDurationSeconds?: number;
  /** Aggregated token usage for the whole task. */
  totalInputTokens?: number;
  totalOutputTokens?: number;
  /** Number of distinct tool calls aggregated from `toolBurst` events. */
  toolCallCount?: number;
  /** Tool failures aggregated from `toolBurst` events. */
  toolFailureCount?: number;
  /** Number of commits attached to the task (or its runs). */
  commitCount?: number;
  /** Number of files changed across the attached commits. */
  filesChanged?: number;
  /** Number of screenshot artefacts. */
  screenshotCount?: number;
  /** Retry / parser / capture-fail warnings raised during the task. */
  retryWarningCount?: number;
  /** True when the watchdog killed at least one run. */
  watchdogKilled?: boolean;
  /** Latest result string (sentinel name + heuristic flag), when known. */
  latestResult?: string;
}

export interface WorkbenchSummaryEvent extends ConversationEventBase {
  kind: 'workbench.summary';
  /** Headline shown in the summary strip ("12 reads · 3 edits · tests passing"). */
  headline: string;
  /** Optional bullets the strip can expand into. */
  bullets?: readonly string[];
  /** Linked drill-down events the right pane can open. */
  drillDowns?: readonly TraceLink[];
  /** Typed aggregate so renderers don't need to scan the event list themselves. */
  aggregate?: WorkbenchSummaryAggregate;
}

export interface WorkbenchGitPreviewEvent extends ConversationEventBase {
  kind: 'workbench.gitPreview';
  /** Files touched within the run that anchors this preview. */
  files: readonly { status: string; path: string; added: number; removed: number }[];
  /** SHA range that the Git split should default to. */
  headShaBefore?: string | null;
  headShaAfter?: string | null;
}

export interface WorkbenchVisualPreviewEvent extends ConversationEventBase {
  kind: 'workbench.visualPreview';
  /** Image set the preview pane should show by default. */
  images: readonly { caption: string; path: string }[];
  /** Optional caption above the strip. */
  groupCaption?: string;
}

/**
 * Read-only debug aggregate that backs the Verbose Debug pane and the
 * status-bar Debug split. The renderer composes these counts into tabs
 * (Overview, Actors, Tools, Tokens, Trace) without re-walking the raw log.
 */
export interface WorkbenchDebugEvent extends ConversationEventBase {
  kind: 'workbench.debug';
  /** Per-actor message counts. */
  actorCounts: {
    user: number;
    taskAgent: number;
    orchestrator: number;
    supervisor: number;
    supportingAgent: number;
  };
  /** Per-family tool call counts plus failures. */
  toolDensity: {
    total: number;
    failures: number;
    families: Partial<Record<ToolFamily, number>>;
  };
  /** Counts of supervisor and parser-warning rows. */
  warningCounts: {
    supervisorAdvisories: number;
    parserWarnings: number;
    captureFails: number;
    schemaDrifts: number;
    needsInputLoops: number;
    watchdogQuiet: number;
    watchdogKills: number;
  };
  /** Token rollup mirroring `metric.token` events the host emitted. */
  tokenTotals: {
    inputTokens: number;
    outputTokens: number;
    reasoningTokens: number;
    cost?: number;
  };
  /** Number of runs in the timeline, plus the count that finished cleanly. */
  runStats: {
    runCount: number;
    completedCount: number;
    failedCount: number;
    cancelledCount: number;
  };
  /** Trace ranges exposed for the "Trace" tab. */
  traceLinks: readonly TraceLink[];
}

/**
 * A schema-drift row is raised when a structured Markdown / JSON report the
 * orchestrator expected (a meta-cycle report, summary template, etc.) cannot
 * be parsed cleanly. Renderers show a human-friendly "Report is unstructured"
 * row with a recovery link.
 */
export interface SystemSchemaDriftEvent extends ConversationEventBase {
  kind: 'system.schemaDrift';
  /** What the parser was expecting (`MetaCycleReport`, `summary-template`, ...). */
  expectedSchema: string;
  /** Short reason from the parser. */
  message: string;
  /** Suggested recovery action ("regenerate report", "open raw"). */
  recovery?: string;
  /** Trace link to the raw blob the parser tried to consume. */
  rawLink?: TraceLink;
}

/**
 * A `feedback.queued` row is the compact marker the user sees after typing
 * into the chat of a task that is no longer being actively worked
 * (`4-auto-review`, `5-human-review`, `6-completed`, `7-archive`). It does
 * not restart the task; it records what the composer decided to do with the
 * comment. The grammar is specified in
 * `docs/mockups/chat-window-next-gen/feedback-queued-from-chat.md`.
 *
 * `Ask` answered the comment read-only (no source mutation); `Defer` queued a
 * follow-up task; `Promote` re-opened the closed task into `3-progress`. The
 * row stays low-emphasis: raw queue routing metadata lives behind Trace, not
 * in the compact body.
 */
export interface FeedbackQueuedEvent extends ConversationEventBase {
  kind: 'feedback.queued';
  /** Which composer mode produced this row. */
  mode: 'ask' | 'defer' | 'promote';
  /** Lane the parent task was in when the user pressed Send. */
  parentLane: '4-auto-review' | '5-human-review' | '6-completed' | '7-archive';
  /**
   * Human-readable marker copy ("I'll get to this when there's bandwidth",
   * "answered inline · no code changes", "merged into follow-up #98"). Plain
   * English — not a sentinel the parser depends on.
   */
  label: string;
  /** For Defer/Promote, the slug of the follow-up (or re-opened) task. */
  followUpJobId?: string | null;
  /** For Ask, true once an inline answer landed. */
  answered?: boolean;
}

export interface TaskMarkerEvent extends ConversationEventBase {
  kind: 'taskMarker';
  /** `start`, `complete`, `review`, `archived`, `noop`, `blocked`, `needs-input`. */
  marker: string;
  jobId: string;
  /** Lane id (`3-progress`, `4-review`, ...). */
  lane?: string;
  title: string;
  /** Total run duration aggregated for the task, when finalised. */
  durationSeconds?: number;
  tokens?: { inputTokens: number; outputTokens: number };
  evidenceLinks?: readonly TraceLink[];
}

export interface RunMarkerEvent extends ConversationEventBase {
  kind: 'runMarker';
  /** `start`, `continue`, `recovery`, `restart`, `complete`, `failed`, `cancelled`. */
  marker: string;
  cli?: string | null;
  model?: string | null;
  sessionId?: string | null;
  durationSeconds?: number | null;
  exitCode?: number | null;
  /** Aggregated token use for the run. */
  tokens?: { inputTokens: number; outputTokens: number };
  /** Trace range that scopes this run for the activity log. */
  traceRange?: RawLineRange;
}

export interface TraceLinkEvent extends ConversationEventBase {
  kind: 'traceLink';
  /** What the row is linking to: `raw-log`, `verbose-debug`, `run`, `screenshots`, `commits`. */
  target: string;
  label: string;
  /** Range or anchor the host should jump to. */
  link: TraceLink;
}

export type ConversationEvent =
  | MessageEvent
  | ToolBurstEvent
  | SupervisorWaitEvent
  | OrchestratorDecisionEvent
  | AgentNeedsInputEvent
  | SystemCaptureFailEvent
  | SystemParserWarningEvent
  | SystemStatusEvent
  | SystemSchemaDriftEvent
  | FeedbackQueuedEvent
  | ArtifactImageEvent
  | MetricTokenEvent
  | WorkbenchSummaryEvent
  | WorkbenchGitPreviewEvent
  | WorkbenchVisualPreviewEvent
  | WorkbenchDebugEvent
  | TaskMarkerEvent
  | RunMarkerEvent
  | TraceLinkEvent;

/** Stable list of all known kinds — used by tests to assert exhaustiveness. */
export const CONVERSATION_EVENT_KINDS: readonly ConversationEventKind[] = [
  'message.user',
  'message.taskAgent',
  'message.orchestrator',
  'message.supervisor',
  'message.supportingAgent',
  'toolBurst',
  'supervisor.wait',
  'decision.orchestrator',
  'agent.needsInput',
  'system.captureFail',
  'system.parserWarning',
  'system.status',
  'system.schemaDrift',
  'feedback.queued',
  'artifact.image',
  'metric.token',
  'workbench.summary',
  'workbench.gitPreview',
  'workbench.visualPreview',
  'workbench.debug',
  'taskMarker',
  'runMarker',
  'traceLink'
];
