/**
 * Cycle 9 project-chat feature models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * One turn returned by the Slice D project-chat surface
 * (`/api/projects/{project}/chat/...`). Wider author + kind enums
 * than the legacy `OrchestratorChatTurn`: the new tree carries
 * embedded events (tool-call / watchdog / rate-limit / ...) as
 * first-class records alongside conventional turns.
 */

export interface ProjectChatTurn {
  turnId: string;
  author:
    | 'user'
    | 'orchestrator'
    | 'agent'
    | 'supervisor'
    | 'claude'
    | 'codex'
    | 'gemini';
  kind:
    | 'turn'
    | 'event-tool-call'
    | 'event-watchdog'
    | 'event-rate-limit'
    | 'event-update'
    | 'event-task'
    | 'event-decision';
  ts: string;
  refs?: string[] | null;
  body: string;
}

export interface ProjectChatScrollResponse {
  project: string;
  direction: 'before' | 'after' | 'tail';
  turns: ProjectChatTurn[];
}

export interface ProjectChatSearchHit {
  turnId: string;
  author: ProjectChatTurn['author'];
  kind: ProjectChatTurn['kind'];
  ts: string;
  /** HTML-safe snippet with `<b>...</b>` highlight markers around matched terms. */
  snippet: string;
  score: number;
}

export interface ProjectChatSearchResponse {
  project: string;
  results: ProjectChatSearchHit[];
}

export interface ProjectChatTurnResponse {
  project: string;
  turn: ProjectChatTurn;
}

/**
 * Returned by /api/projects/{project}/chat/stats. Drives the
 * step-load panel headline ("47,238 messages total, you are viewing
 * 1,000 of them, going back to 2026-04-15").
 */
export interface ProjectChatStatsResponse {
  project: string;
  totalCount: number;
  oldestTs: string | null;
  newestTs: string | null;
}
