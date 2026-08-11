import type { OrchestratorChatTurn } from '../../../../features/orchestrator';
import type { ChatEvent, ConversationEvent, RawLineRange } from 'coding-agent-chat/core';

/**
 * Pure helpers for the orchestrator side sheet. Extracted from the
 * component controller so the component .ts stays within its size budget
 * while the navigation-context / pin logic (MC-2) lives inline where it
 * belongs. These functions carry no Angular dependency and are unit-tested
 * directly.
 */

/**
 * Hide any server user turn that an in-flight local turn already represents.
 *
 * After the operator hits Send we render a local "optimistic" turn so the
 * bubble shows up immediately. When the round-trip finishes the server
 * reports the same user turn while the local turn can still be on screen.
 *
 * Match strategy: walk local user turns newest-to-oldest and pair each
 * with the newest unmatched server user turn that has the same text.
 * Pairing is greedy and one-shot so
 * sending the same message twice in a row only suppresses one copy per
 * local turn.
 */
export function suppressLocalDuplicates(
  server: readonly OrchestratorChatTurn[],
  local: readonly OrchestratorChatTurn[]
): OrchestratorChatTurn[] {
  if (local.length === 0) return [...server];
  const localUsers = local.filter((t) => t.role === 'user');
  if (localUsers.length === 0) return [...server];
  const suppress = new Set<string>();
  for (const lt of localUsers) {
    for (let i = server.length - 1; i >= 0; i--) {
      const st = server[i];
      if (suppress.has(st.id)) continue;
      if (st.role !== 'user') continue;
      if ((st.text ?? '') !== (lt.text ?? '')) continue;
      suppress.add(st.id);
      break;
    }
  }
  return suppress.size === 0 ? [...server] : server.filter((s) => !suppress.has(s.id));
}

export type OptimisticOrchestratorChatTurn = OrchestratorChatTurn & {
  pending?: boolean;
};

/**
 * Signal equality for the polled server transcript.
 *
 * The endpoint deserializes a fresh object graph on every heartbeat even when
 * no turn changed. Treating that graph as new makes the conversation projection
 * and every Markdown host run again; Studio's task-reference hydrator then has
 * to replace the same AGT-* text with the same microcards, which is visible as
 * selective pill flicker. Compare the complete persisted turn contract so a
 * real text, metadata, token, or error change still propagates.
 */
export function sameOrchestratorChatTurns(
  previous: readonly OrchestratorChatTurn[],
  current: readonly OrchestratorChatTurn[],
): boolean {
  if (previous === current) return true;
  if (previous.length !== current.length) return false;
  return previous.every((left, index) => {
    const right = current[index];
    return left.id === right.id
      && left.ts === right.ts
      && left.role === right.role
      && left.text === right.text
      && (left.model ?? null) === (right.model ?? null)
      && (left.errorMessage ?? null) === (right.errorMessage ?? null)
      && sameContextReceipt(left.contextReceipt, right.contextReceipt)
      && sameTokenUsage(left.tokenUsage, right.tokenUsage);
  });
}

function sameContextReceipt(
  left: OrchestratorChatTurn['contextReceipt'],
  right: OrchestratorChatTurn['contextReceipt'],
): boolean {
  if (left === right) return true;
  if (!left || !right) return left == null && right == null;
  return left.scope === right.scope
    && left.contextKey === right.contextKey
    && (left.taskKey ?? null) === (right.taskKey ?? null)
    && left.capturedAt === right.capturedAt
    && left.includedBlocks.length === right.includedBlocks.length
    && left.includedBlocks.every((block, index) => block === right.includedBlocks[index]);
}

function sameTokenUsage(
  left: OrchestratorChatTurn['tokenUsage'],
  right: OrchestratorChatTurn['tokenUsage'],
): boolean {
  if (left === right) return true;
  if (!left || !right) return left == null && right == null;
  return (left.model ?? null) === (right.model ?? null)
    && (left.thinkingLevel ?? null) === (right.thinkingLevel ?? null)
    && left.inputTokens === right.inputTokens
    && left.outputTokens === right.outputTokens
    && left.cacheReadTokens === right.cacheReadTokens
    && left.cacheCreationTokens === right.cacheCreationTokens;
}

/**
 * Project the side sheet's transport-specific transcript into the canonical
 * `coding-agent-chat` conversation grammar.
 *
 * The orchestrator endpoint returns simple user/orchestrator turns while the
 * optimistic path temporarily holds a second, local representation of the
 * newest user turn. The next-gen conversation view must receive one ordered
 * event stream, so this adapter suppresses that overlap and translates the
 * legacy inline event-card contract into the closest semantic event kind.
 */
export function buildOrchestratorConversationEvents(
  serverTurns: readonly OrchestratorChatTurn[],
  localTurns: readonly OptimisticOrchestratorChatTurn[],
  inlineEvents: readonly ChatEvent[],
  source: string,
): ConversationEvent[] {
  const persisted = suppressLocalDuplicates(serverTurns, localTurns);
  const turns: readonly OptimisticOrchestratorChatTurn[] = [...persisted, ...localTurns];
  const projected: { event: ConversationEvent; inputIndex: number }[] = [];

  turns.forEach((turn, index) => {
    const error = turn.errorMessage?.trim();
    const body = error
      ? `${turn.text ? `${turn.text}\n\n` : ''}**Error:** ${error}`
      : turn.text;

    projected.push({
      inputIndex: index,
      event: {
        id: turn.id,
        kind: turn.role === 'user' ? 'message.user' : 'message.orchestrator',
        timestamp: turn.ts,
        severity: error ? 'error' : undefined,
        model: turn.model ?? turn.tokenUsage?.model ?? null,
        thinkingLevel: turn.tokenUsage?.thinkingLevel ?? null,
        rawRange: rangeFor(source, index),
        body,
        actor: turn.role === 'user' ? 'You' : 'Orchestrator',
      },
    });

  });

  inlineEvents.forEach((event, index) => {
    const inputIndex = turns.length + index;
    projected.push({
      inputIndex,
      event: projectInlineEvent(event, rangeFor(source, inputIndex)),
    });
  });

  return projected
    .sort((left, right) => compareTimestamp(left.event.timestamp, right.event.timestamp)
      || left.inputIndex - right.inputIndex)
    .map(item => item.event);
}

function projectInlineEvent(event: ChatEvent, rawRange: RawLineRange): ConversationEvent {
  const base = {
    id: event.id,
    timestamp: event.timestamp,
    severity: event.severity,
    rawRange,
  } as const;

  switch (event.kind) {
    case 'tool-call':
      return {
        ...base,
        kind: 'toolBurst',
        count: 1,
        families: { other: 1 },
        failures: event.severity === 'error' ? 1 : 0,
        durationMs: 0,
        samples: { other: event.summary },
        collapsedByDefault: true,
      };
    case 'watchdog':
      return {
        ...base,
        kind: 'supervisor.wait',
        state: event.severity === 'error' || /\bkill(?:ed)?\b/i.test(event.summary) ? 'killed' : 'quiet',
        quietSeconds: secondsFrom(event.summary),
        reason: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
      };
    case 'session-recovered':
      return {
        ...base,
        kind: 'supervisor.wait',
        state: 'resumed',
        quietSeconds: 0,
        reason: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
      };
    case 'decision':
      return {
        ...base,
        kind: 'decision.orchestrator',
        decisionType: event.decisionType ?? 'decision',
        reason: event.summary,
        evidence: event.detail,
        action: event.actionLabel,
      };
    case 'rate-limit':
    case 'update':
    case 'task':
    case 'memory-refreshed':
      return {
        ...base,
        kind: 'system.status',
        category: event.kind,
        label: inlineEventLabel(event.kind),
        explanation: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
        nextStep: event.actionLabel,
      };
  }
}

function rangeFor(source: string, index: number): RawLineRange {
  const line = index + 1;
  return { source, start: line, end: line };
}

function compareTimestamp(left: string, right: string): number {
  const leftMs = Date.parse(left);
  const rightMs = Date.parse(right);
  if (!Number.isFinite(leftMs) || !Number.isFinite(rightMs)) return 0;
  return leftMs - rightMs;
}

function secondsFrom(summary: string): number {
  const match = /\b(\d+)\s*s(?:ec(?:ond)?s?)?\b/i.exec(summary);
  return match ? Number(match[1]) : 0;
}

function inlineEventLabel(kind: Extract<ChatEvent['kind'], 'rate-limit' | 'update' | 'task' | 'memory-refreshed'>): string {
  switch (kind) {
    case 'rate-limit': return 'Rate limit';
    case 'update': return 'Update';
    case 'task': return 'Task';
    case 'memory-refreshed': return 'Memory refreshed';
  }
}
