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

/**
 * Super-phase: an outer block grouping consecutive ChatPhases that
 * belong to the same "working session". Boundary rule (deterministic):
 * an idle gap of >= `idleBoundaryMs` between the end of one phase and
 * the start of the next opens a new super-phase. The default 15-minute
 * gap matches the operator's mental model of a coherent burst of work
 * vs. coming back later. Always one super-phase per phase when the
 * conversation is contiguous.
 */
export interface SuperPhase {
  /** Stable id derived from the first contained phase. */
  id: string;
  /** ISO timestamp of the super-phase's first phase. */
  startTs: string;
  /** ISO timestamp of the super-phase's last phase. */
  endTs: string;
  /** Contained phases in chronological order (length >= 1). */
  phases: readonly ChatPhase[];
  /** Union of distinct participants across all contained phases, first-seen order. */
  participants: readonly WorkforceRole[];
  /** Sum of messageCount across contained phases. */
  messageCount: number;
  /** Deterministic one-liner (phases × messages × duration). */
  summary: string;
}

export interface SuperPhaseGroupingOptions {
  /** Min idle (ms) between two phases that opens a new super-phase. Default 15 min. */
  idleBoundaryMs?: number;
}

const DEFAULT_IDLE_BOUNDARY_MS = 15 * 60 * 1000;

/**
 * Group ordered phases into super-phases. Input must be in chronological
 * order (the natural output of {@link groupIntoPhases}).
 */
export function groupIntoSuperPhases(
  phases: readonly ChatPhase[],
  opts?: SuperPhaseGroupingOptions
): SuperPhase[] {
  if (phases.length === 0) return [];
  const boundaryMs = opts?.idleBoundaryMs ?? DEFAULT_IDLE_BOUNDARY_MS;
  const groups: ChatPhase[][] = [];
  let bucket: ChatPhase[] = [phases[0]];
  for (let i = 1; i < phases.length; i++) {
    const prev = phases[i - 1];
    const cur = phases[i];
    const gap = parseTs(cur.startTs) - parseTs(prev.endTs);
    if (gap >= boundaryMs) {
      groups.push(bucket);
      bucket = [cur];
    } else {
      bucket.push(cur);
    }
  }
  groups.push(bucket);
  return groups.map(buildSuperPhase);
}

function buildSuperPhase(phases: ChatPhase[]): SuperPhase {
  const first = phases[0];
  const last = phases[phases.length - 1];
  const seen = new Set<WorkforceRoleId>();
  const participants: WorkforceRole[] = [];
  let messageCount = 0;
  for (const phase of phases) {
    messageCount += phase.messageCount;
    for (const role of phase.participants) {
      if (seen.has(role.id)) continue;
      seen.add(role.id);
      participants.push(role);
    }
  }
  return {
    id: `super-${first.id}`,
    startTs: first.startTs,
    endTs: last.endTs,
    phases,
    participants,
    messageCount,
    summary: buildSuperSummary(phases.length, messageCount, first.startTs, last.endTs),
  };
}

function buildSuperSummary(
  phaseCount: number,
  messageCount: number,
  startTs: string,
  endTs: string
): string {
  const durMs = Math.max(0, parseTs(endTs) - parseTs(startTs));
  const dur = formatDuration(durMs);
  return `${phaseCount} ${pluralize('phase', phaseCount)} · ${messageCount} ${pluralize('message', messageCount)} · ${dur}`;
}

function parseTs(iso: string): number {
  const t = Date.parse(iso);
  return Number.isFinite(t) ? t : 0;
}

function formatDuration(ms: number): string {
  if (ms < 60_000) return '< 1 min';
  const totalMin = Math.round(ms / 60_000);
  if (totalMin < 60) return `${totalMin} min`;
  const h = Math.floor(totalMin / 60);
  const m = totalMin % 60;
  return m === 0 ? `${h} h` : `${h} h ${m} min`;
}
