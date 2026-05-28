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
