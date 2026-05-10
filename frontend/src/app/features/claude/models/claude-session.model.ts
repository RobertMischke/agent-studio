/**
 * Cycle 9 claude feature models. Lifted out of `models/job.model.ts`
 * per ADR-0034. Re-exported from the legacy file so existing imports
 * keep working; new code should import from this feature folder.
 */

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
