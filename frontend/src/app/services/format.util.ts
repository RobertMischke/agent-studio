import { CliType } from '../models/job.model';

/**
 * Pure formatting helpers used by both the board and detail views.
 *
 * Kept dependency-free so they can be imported from any component or
 * service without DI; they MUST stay side-effect free (no signals, no
 * `Date.now()` calls — pass `now` explicitly when relative times are
 * needed; that's what avoids NG0100 in change-detection passes).
 */

export function formatTokens(n: number): string {
  if (!n) return '0';
  if (n < 1000) return String(n);
  if (n < 1_000_000) return (n / 1000).toFixed(1) + 'k';
  return (n / 1_000_000).toFixed(2) + 'M';
}

export function formatRateWindow(window: string | null): string {
  if (!window) return '?';
  return window.replace(/_/g, '-');
}

/**
 * `now` is passed in (rather than read via Date.now()) so the result is
 * stable across the same change-detection cycle. Callers should source
 * `now` from a tick-signal (`NowTickService`).
 */
export function formatResetIn(epochSeconds: number, now: number): string {
  if (!epochSeconds) return '?';
  const ms = epochSeconds * 1000 - now;
  if (ms <= 0) return 'now';
  const min = Math.floor(ms / 60_000);
  if (min < 2) return `${Math.floor(ms / 1000)}s`;
  if (min < 120) return `in ${min} min`;
  const hrs = ms / 3_600_000;
  if (hrs < 48) return `in ${hrs.toFixed(1)} h`;
  return `in ${Math.floor(hrs / 24)} d`;
}

export function stateLabel(state: string): string {
  return state.replace(/^\d+-/, '');
}

export function formatTime(dateStr: string): string {
  return new Date(dateStr).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function formatDate(dateStr: string): string {
  return new Date(dateStr).toLocaleDateString();
}

export function formatDateTime(dateStr: string): string {
  return new Date(dateStr).toLocaleString([], {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  });
}

export function formatMultiplier(mult: number | null): string {
  if (mult === null) return '';
  return mult === 0 ? '0×' : `${mult}×`;
}

export function cliTypeLabel(t: CliType): string {
  switch (t) {
    case 'copilot': return 'Copilot';
    case 'claude':  return 'Claude Code';
    case 'codex':   return 'Codex';
    case 'gemini':  return 'Gemini';
  }
}

// Distinct glyph per CLI so the cost overview, job preview cards, and
// command-deck picker can be told apart at a glance. Choices echo each
// vendor's mark: Anthropic burst (Claude), OpenAI knot (Codex), GitHub
// Octocat (Copilot), Gemini zodiac (Gemini).
export function cliTypeIcon(t: CliType): string {
  switch (t) {
    case 'copilot': return '🐙';
    case 'claude':  return '✴️';
    case 'codex':   return '🌀';
    case 'gemini':  return '♊';
  }
}
