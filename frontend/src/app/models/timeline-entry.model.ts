/**
 * Unified timeline entry used by the project Observability panel.
 *
 * Three legacy sources feed activity into the UI today:
 *
 * - **Agent Message Bus** (`AgentMessage`): observability spine, append-only
 *   JSONL on disk, exposed via `/api/bus/...`.
 * - **Project Chat** (`ProjectChatEntry`): per-project markdown chat, exposed
 *   via `/api/project-chat/...`.
 * - **Orchestrator Chat** (`OrchestratorChatEntry`): per-project decisions
 *   the orchestrator wrote into `orchestrator-chat.jsonl`.
 *
 * Each has its own shape; the panel needs ONE chronological list. This
 * module defines the common envelope and a set of adapters that fold each
 * source onto it. The adapter is intentionally lossless on the source side -
 * `TimelineEntry.source` plus `TimelineEntry.raw` lets a consumer drill back
 * to the original record without re-querying.
 *
 * Design constraints:
 * - Adapters are pure functions. No IO, no globals. Trivially memoisable.
 * - `TimelineEntry` does NOT extend `AgentMessage`; we keep the bus shape
 *   intact so the bus model can evolve without breaking the unified view.
 * - Sort order is by `createdAt` ascending; ties broken by `id` (lexical).
 */

import type { AgentMessage, AgentMessageLatency, AgentMessageTokens } from './agent-bus.model';

export type TimelineSource = 'bus' | 'project-chat' | 'orchestrator-chat';

export type TimelineKind =
  | 'observation'
  | 'question'
  | 'decision'
  | 'advisory'
  | 'intervention'
  | 'artifact'
  | 'token-usage'
  | 'lifecycle'
  | 'error'
  | 'heartbeat'
  | 'chat';

export type TimelineSeverity = 'Info' | 'Warn' | 'High';

export interface TimelineEntry {
  /** Stable id - the bus message id, or `chat:<entryId>` for project chat. */
  id: string;
  /** ISO UTC string; the only field consumers should sort on. */
  createdAt: string;
  /** Where the entry originated; used for filter chips and drill-back. */
  source: TimelineSource;
  /** Normalised kind. Bus kinds pass through; chat entries map to `chat`. */
  kind: TimelineKind;
  severity: TimelineSeverity;
  participantId: string;
  participantLabel?: string | null;
  project?: string | null;
  jobId?: string | null;
  runId?: string | null;
  topic?: string | null;
  summary: string;
  body?: string | null;
  tokens?: AgentMessageTokens | null;
  latency?: AgentMessageLatency | null;
  tags?: string[] | null;
  /** Pointer to the original record. Consumers should not write into this. */
  raw: unknown;
}

/** Adapter: AgentMessage -> TimelineEntry. */
export function busMessageToTimelineEntry(m: AgentMessage): TimelineEntry {
  return {
    id: m.id,
    createdAt: m.createdAt,
    source: 'bus',
    kind: (m.kind as TimelineKind) ?? 'observation',
    severity: (m.severity as TimelineSeverity) ?? 'Info',
    participantId: m.participantId,
    project: m.project ?? null,
    jobId: m.jobId ?? null,
    runId: m.runId ?? null,
    topic: m.topic ?? null,
    summary: m.summary ?? '(empty)',
    body: m.body ?? null,
    tokens: m.tokens ?? null,
    latency: m.latency ?? null,
    tags: m.tags ?? null,
    raw: m,
  };
}

/**
 * Adapter for a project-chat entry. Kept loose-typed because the chat
 * shape lives in its own service and bringing it here would couple two
 * unrelated models. Callers pass the fields they have; missing fields
 * fall back to sane defaults.
 */
export interface ProjectChatLike {
  id: string;
  createdAt: string;
  author: string;
  message: string;
  project?: string | null;
  jobId?: string | null;
  tags?: string[] | null;
}

export function projectChatToTimelineEntry(c: ProjectChatLike): TimelineEntry {
  return {
    id: `chat:${c.id}`,
    createdAt: c.createdAt,
    source: 'project-chat',
    kind: 'chat',
    severity: 'Info',
    participantId: c.author,
    project: c.project ?? null,
    jobId: c.jobId ?? null,
    topic: null,
    summary: truncate(c.message, 280),
    body: c.message,
    tags: c.tags ?? null,
    raw: c,
  };
}

/** Orchestrator-chat shape (subset; mirrors `OrchestratorChatEntry`). */
export interface OrchestratorChatLike {
  id: string;
  createdAt: string;
  jobId?: string | null;
  topic?: string | null;
  text: string;
  kind?: string | null;
}

export function orchestratorChatToTimelineEntry(c: OrchestratorChatLike): TimelineEntry {
  return {
    id: `orch:${c.id}`,
    createdAt: c.createdAt,
    source: 'orchestrator-chat',
    kind: 'decision',
    severity: 'Info',
    participantId: 'orchestrator',
    jobId: c.jobId ?? null,
    topic: c.topic ?? c.kind ?? null,
    summary: truncate(c.text, 280),
    body: c.text,
    raw: c,
  };
}

/** Stable comparator for chronological ordering. */
export function compareTimelineEntries(a: TimelineEntry, b: TimelineEntry): number {
  if (a.createdAt !== b.createdAt) return a.createdAt < b.createdAt ? -1 : 1;
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

/**
 * Merge any number of pre-sorted streams into one sorted stream. Adapters
 * produce per-source lists; the panel calls this to interleave them.
 * O(n log k) where n is total entries and k is the number of streams.
 */
export function mergeTimelineStreams(...streams: TimelineEntry[][]): TimelineEntry[] {
  const merged: TimelineEntry[] = [];
  for (const s of streams) merged.push(...s);
  merged.sort(compareTimelineEntries);
  return merged;
}

function truncate(s: string, max: number): string {
  if (!s) return '(empty)';
  if (s.length <= max) return s;
  return s.slice(0, max - 1) + '…';
}
