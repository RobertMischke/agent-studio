/**
 * Cycle 9 session-events feature models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * One row per `logs/session-events.jsonl` line for a job: the runner
 * writes a `start` / `continue` / `recovery` row each time the CLI
 * actually launches, with the input + captured session ids and the
 * resume flag the planner used. The protocol pane reads this to render
 * the per-job session chain badge.
 */

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
 * Per-job rollup of "what the agent actually did" - folded from
 * `logs/session-events.jsonl` (one row per CLI start / continue /
 * recovery) and `logs/tool-calls.jsonl` (one row per tool started /
 * completed). Drives the Overview tab's Agent Work block; mirrors
 * backend `AgentWorkSummary`.
 */
export interface AgentWorkSummary {
  calls: number;
  recovered: boolean;
  toolCalls: number;
  toolCounts: AgentWorkToolCount[];
  startedAt: string | null;
  lastTouchAt: string | null;
  currentSessionId: string | null;
}

export interface AgentWorkToolCount {
  tool: string;
  count: number;
}

/**
 * Drill-down companion to `AgentWorkSummary`: the same `tool-calls.jsonl`
 * rows folded into per-tool groups, each carrying the individual calls so
 * the Overview tab can show *what* the agent did (command / file / pattern)
 * in a grouped, expandable view. Mirrors backend `AgentWorkDetail`.
 */
export interface AgentWorkDetail {
  groups: AgentWorkToolGroup[];
  /** Total started tool-call rows across all groups (uncapped). */
  totalCalls: number;
}

export interface AgentWorkToolGroup {
  tool: string;
  /** Full started count; may exceed `calls.length` when the call list is capped. */
  count: number;
  calls: AgentWorkCall[];
}

export interface AgentWorkCall {
  /** ISO timestamp of the started row. */
  ts: string | null;
  /** Shell command, file path, grep pattern, etc. May be empty. */
  argument: string | null;
  /** True once a matching completed row was observed. */
  completed: boolean;
  /** From the completed row: true when the tool reported an error. */
  isError: boolean | null;
  /** From the completed row: first line of the tool result, when captured. */
  resultFirstLine: string | null;
}
