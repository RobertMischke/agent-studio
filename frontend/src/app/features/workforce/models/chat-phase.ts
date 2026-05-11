/**
 * Phase grouping for the compressed workforce-chat summary layer.
 *
 * A "phase" is a contiguous block of chat messages around a decision -
 * typically a user steer followed by the workforce's reply rotation, or
 * an autonomous orchestrator-driven block between two user messages. The
 * compressed summary layer above the verbatim chat renders one collapsible
 * line per phase.
 *
 * Grouping rule (deterministic):
 *   - A user message starts a new phase. Phases are user-anchored on
 *     purpose: the operator's mental model is "I steered, then the
 *     workforce reacted" - that whole reaction is one phase.
 *   - Messages without a preceding user message in the loaded window
 *     form an implicit leading phase (the "before you spoke" history).
 *   - Within a phase, the participants list is the set of distinct
 *     roles that contributed, in first-seen order.
 *
 * The summary line itself is built deterministically from the participant
 * list and the phase length so it is stable across renders - the prompt
 * calls for a separately-generated, cached agent summary as a future
 * enrichment; the deterministic shape here is the substrate the future
 * agent step replaces. Callers cache phases keyed by phase id so the
 * grouping pass does not re-run on every render.
 */

import { resolveRole, type RoleAttributionInput, type WorkforceRole, type WorkforceRoleId } from './workforce-role';

/**
 * Generic chat row the grouping helper accepts. Mirrors both
 * `ProjectChatTurn` (project chat) and `ChatMessage` (task chat) so the
 * helper stays renderer-agnostic.
 */
export interface PhaseInputMessage {
  /** Stable id - reused as part of the phase id. */
  id: string;
  /** ISO timestamp. Used only for the rendered "from→to" range. */
  ts: string;
  /** Author label (user / orchestrator / agent / ...). */
  author?: string | null;
  /** Optional message kind (`turn` / `event-*` / ...). */
  kind?: string | null;
  /** Optional refs (aspect:..., role:...). */
  refs?: readonly string[] | null;
  /** Optional pre-resolved role id from the projection. */
  roleId?: WorkforceRoleId | null;
}

export interface ChatPhase {
  /** Stable id derived from the first message in the phase. */
  id: string;
  /** ISO timestamp of the first message in the phase. */
  startTs: string;
  /** ISO timestamp of the last message in the phase. */
  endTs: string;
  /** Distinct roles seen in this phase, in first-seen order. */
  participants: readonly WorkforceRole[];
  /** Message ids that belong to this phase, in chronological order. */
  messageIds: readonly string[];
  /** Pure-function one-line summary. The future agent step replaces this body in place. */
  summary: string;
  /** True when the phase has at least one user turn (user-anchored). */
  hasUser: boolean;
  /** Total number of messages in the phase. */
  messageCount: number;
}

/**
 * Group a chronological message list into phases. The input must be
 * sorted oldest-first; callers that hold a reverse-chronological window
 * must reverse before calling.
 */
export function groupIntoPhases(messages: readonly PhaseInputMessage[]): ChatPhase[] {
  if (messages.length === 0) return [];
  const phases: ChatPhase[] = [];
  let current: {
    firstId: string;
    startTs: string;
    endTs: string;
    messageIds: string[];
    roles: WorkforceRole[];
    seenRoleIds: Set<WorkforceRoleId>;
    hasUser: boolean;
  } | null = null;

  const flush = (): void => {
    if (!current) return;
    phases.push(buildPhase(current));
    current = null;
  };

  for (const msg of messages) {
    const role = resolveRole(roleInput(msg));
    const isUser = role.id === 'user';

    if (current && isUser) {
      flush();
    }
    if (!current) {
      current = {
        firstId: msg.id,
        startTs: msg.ts,
        endTs: msg.ts,
        messageIds: [],
        roles: [],
        seenRoleIds: new Set<WorkforceRoleId>(),
        hasUser: false,
      };
    }
    current.endTs = msg.ts;
    current.messageIds.push(msg.id);
    if (!current.seenRoleIds.has(role.id)) {
      current.roles.push(role);
      current.seenRoleIds.add(role.id);
    }
    if (isUser) current.hasUser = true;
  }
  flush();
  return phases;
}

function roleInput(msg: PhaseInputMessage): RoleAttributionInput {
  return {
    author: msg.author ?? null,
    kind: msg.kind ?? null,
    refs: msg.refs ?? null,
    roleId: msg.roleId ?? null,
  };
}

function buildPhase(state: {
  firstId: string;
  startTs: string;
  endTs: string;
  messageIds: string[];
  roles: WorkforceRole[];
  seenRoleIds: Set<WorkforceRoleId>;
  hasUser: boolean;
}): ChatPhase {
  return {
    id: `phase-${state.firstId}`,
    startTs: state.startTs,
    endTs: state.endTs,
    participants: state.roles,
    messageIds: state.messageIds,
    summary: buildSummary(state.roles, state.messageIds.length, state.hasUser),
    hasUser: state.hasUser,
    messageCount: state.messageIds.length,
  };
}

/**
 * Pure-function summary string. Deterministic; same inputs → same
 * output. The future "small dedicated agent step" replaces the body of
 * this function with a cached LLM-generated sentence per phase; the
 * grouping shape and the participant list stay the same so the renderer
 * never has to special-case "agent summary ready" vs. "not ready".
 */
export function buildSummary(
  participants: readonly WorkforceRole[],
  messageCount: number,
  hasUser: boolean
): string {
  // Strip the user from the displayed participant chain - the operator
  // does not need to read "You opened the phase" in their own summary.
  const workforce = participants.filter((r) => r.id !== 'user');
  if (workforce.length === 0) {
    if (hasUser) return `You opened the conversation (${messageCount} ${pluralize('message', messageCount)}).`;
    return `Empty phase (${messageCount} ${pluralize('message', messageCount)}).`;
  }

  const names = workforce.map((r) => r.label);
  const chain =
    names.length === 1
      ? names[0]
      : names.length === 2
        ? `${names[0]} and ${names[1]}`
        : `${names.slice(0, -1).join(', ')}, then ${names[names.length - 1]}`;

  const opener = hasUser ? 'You steered;' : '';
  return [opener, `${chain} responded`, `(${messageCount} ${pluralize('message', messageCount)}).`]
    .filter(Boolean)
    .join(' ');
}

function pluralize(word: string, n: number): string {
  return n === 1 ? word : `${word}s`;
}
