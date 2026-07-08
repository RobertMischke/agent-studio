import type { ChatContextUsage, ChatContextSection } from 'coding-agent-chat/core';
import type { ContextUsageSnapshot } from '../../models/task.model';

/**
 * Maps the backend's parsed `/context` snapshot (string metrics/sections)
 * onto the numeric {@link ChatContextUsage} contract the library's
 * `<cac-context-ring>` renders.
 *
 * The CLI's `/context` output is prose, so this is a tolerant extraction:
 * - used/max come from the first "<n>[k|m] / <n>[k|m] tokens" occurrence
 *   anywhere in the raw text (e.g. "76k/200k tokens (38%)");
 * - breakdown rows come from metrics whose value starts with a token count
 *   (e.g. "Messages: 55.1k tokens (27.6%)").
 *
 * Returns null when no used/max pair can be found — the ring then renders
 * its empty state and the Refresh affordance still works.
 */
export function toChatContextUsage(snapshot: ContextUsageSnapshot | null): ChatContextUsage | null {
  if (!snapshot) return null;
  const haystack = [
    snapshot.rawText ?? '',
    ...snapshot.metrics.map((m) => `${m.label}: ${m.value}`),
  ].join('\n');

  const pair = /(\d+(?:[.,]\d+)?)\s*([km]?)\s*\/\s*(\d+(?:[.,]\d+)?)\s*([km]?)\s*tokens?/i.exec(haystack);
  if (!pair) return null;
  const usedTokens = tokenCount(pair[1], pair[2]);
  const maxTokens = tokenCount(pair[3], pair[4]);
  if (!(maxTokens > 0)) return null;

  const sections: ChatContextSection[] = [];
  for (const metric of snapshot.metrics) {
    const value = /^\s*(\d+(?:[.,]\d+)?)\s*([km]?)\s*tokens?\b/i.exec(metric.value);
    if (!value) continue;
    sections.push({ label: metric.label, tokens: tokenCount(value[1], value[2]) });
  }

  return {
    usedTokens,
    maxTokens,
    sections: sections.length > 0 ? sections : undefined,
    capturedAt: snapshot.at,
    sourceLabel: snapshot.command ? `via ${snapshot.command}` : undefined,
  };
}

function tokenCount(value: string, unit: string): number {
  const n = Number.parseFloat(value.replace(',', '.'));
  if (!Number.isFinite(n)) return 0;
  switch (unit.toLowerCase()) {
    case 'k': return Math.round(n * 1_000);
    case 'm': return Math.round(n * 1_000_000);
    default: return Math.round(n);
  }
}
