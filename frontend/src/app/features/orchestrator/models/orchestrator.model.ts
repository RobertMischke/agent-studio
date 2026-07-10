/**
 * Cycle 9 orchestrator feature models. Lifted out of `models/job.model.ts`
 * per ADR-0034. Re-exported from the legacy file so existing imports
 * keep working; new code should import from this feature folder.
 *
 * Covers the per-project orchestrator log feed, the long-lived
 * orchestrator session, and the orchestrator chat (manager-style
 * conversation alongside the agent runs).
 */

/**
 * One entry in the orchestrator log feed for a project. Mirrors backend
 * `OrchestratorLogEntry`. Kinds: decision / action / observation /
 * intervention. Topics group entries in the UI feed.
 */
export interface OrchestratorLogEntry {
  /** Present on workspace-wide feed responses. */
  project?: string;
  watchPath?: string;
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

export interface GlobalOrchestratorFeedResponse {
  entries: OrchestratorLogEntry[];
}

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
  /**
   * Base64-encoded image bytes for the multimodal fast path. When set, the
   * backend hands the bytes straight to Claude as an image content block in
   * the same user message as the text - no Read tool call needed. Optional;
   * the field is dropped before the turn is persisted so the audit log
   * stays text-only.
   */
  inlineBase64?: string | null;
  /** MIME type of {@link inlineBase64}, e.g. `image/png`. */
  mimeType?: string | null;
}

export interface OrchestratorChatResponse {
  project: string;
  turns: OrchestratorChatTurn[];
}

/**
 * Navigation context the frontend sends with every project-chat POST so the
 * orchestrator can answer context-dependent questions ("what is the current
 * task?", "explain this") against the page the operator is actually on.
 *
 * Background: before this field existed the chat agent answered context
 * questions in vacuum and hallucinated freely (2026-05-09 "Conversation,
 * Foul Conversation" incident). Every field is optional; the backend treats
 * a missing `currentTaskId` as "no task in scope" and the agent must not
 * invent one.
 */
export interface ChatNavigationContext {
  currentPage?: string | null;
  currentTaskId?: string | null;
  currentTaskTitle?: string | null;
  currentTaskState?: string | null;
  currentLaneFilter?: string | null;
  viewportTimestamp?: string | null;
}
