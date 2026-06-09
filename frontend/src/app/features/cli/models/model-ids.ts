// Frontend-only ids for fixture data and initial component state before the
// server catalog/defaults load. Selectable model lists still come from /api/cli.
export const MODEL_IDS = {
  claudeOpus47: 'claude-opus-4-7',
  claudeHaiku45: 'claude-haiku-4-5',
  claudeSonnet46: 'claude-sonnet-4-6',
  gpt5Codex: 'gpt-5-codex',
} as const;

export const CLAUDE_FALLBACK_MODEL_ID = MODEL_IDS.claudeHaiku45;
