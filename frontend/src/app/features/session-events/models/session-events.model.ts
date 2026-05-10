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
