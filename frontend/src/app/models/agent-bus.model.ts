/**
 * Frontend mirror of the Agent Message Bus contract
 * (`docs/schemas/agent-message.schema.json`, `backend/Models/AgentBus.cs`).
 * The bus is observability-only: it does not move state, never blocks the
 * runner, and is safe to read from the project Observability panel without
 * any side effects.
 */

export interface AgentMessageTokens {
  input: number;
  output: number;
  cacheRead?: number | null;
  cacheWrite?: number | null;
  model?: string | null;
  dollars?: number | null;
}

export interface AgentArtifactRef {
  kind: string;
  uri: string;
  label?: string | null;
  byteRange?: { start: number; end: number } | null;
  lineRange?: { start: number; end: number } | null;
  sha256?: string | null;
  bytes?: number | null;
}

export interface AgentMessage {
  schemaVersion: number;
  id: string;
  createdAt: string;
  participantId: string;
  role: string;
  kind: string;
  severity?: string | null;
  project?: string | null;
  jobId?: string | null;
  runId?: string | null;
  cliSessionId?: string | null;
  topic?: string | null;
  summary?: string | null;
  body?: string | null;
  replyToId?: string | null;
  correlationId?: string | null;
  tokens?: AgentMessageTokens | null;
  artifacts?: AgentArtifactRef[] | null;
  payload?: unknown;
  tags?: string[] | null;
}

export interface AgentMessageSummary {
  project: string;
  totalMessages: number;
  firstMessageAt?: string | null;
  lastMessageAt?: string | null;
  countsByKind: Record<string, number>;
  countsByParticipant: Record<string, number>;
  countsBySeverity: Record<string, number>;
}

export interface AgentMessageQuery {
  jobId?: string | null;
  runId?: string | null;
  participantId?: string | null;
  kind?: string | null;
  severity?: string | null;
  cli?: string | null;
  skill?: string | null;
  tag?: string | null;
  correlationId?: string | null;
  since?: string | null;
  until?: string | null;
  limit?: number | null;
}

/** Allowed enum values, mirrored from the backend validator. */
export const AGENT_MESSAGE_KINDS = [
  'observation',
  'question',
  'decision',
  'advisory',
  'intervention',
  'artifact',
  'token-usage',
  'lifecycle',
  'error',
  'heartbeat',
] as const;

export const AGENT_MESSAGE_SEVERITIES = ['Info', 'Warn', 'High'] as const;
