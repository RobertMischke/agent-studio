import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { MarkdownViewComponent } from '../../markdown-view/markdown-view.component';
import { ToolBurstChipComponent } from '../tool-burst-chip/tool-burst-chip.component';
import { TooltipDirective } from '../../tooltip';
import type { StructuredTooltip } from '../../tooltip';
import type {
  AgentNeedsInputEvent,
  ArtifactImageEvent,
  ConversationEvent,
  ConversationEventSeverity,
  FeedbackQueuedEvent,
  MessageEvent,
  MetricTokenEvent,
  OrchestratorDecisionEvent,
  RawLineRange,
  RunMarkerEvent,
  SupervisorWaitEvent,
  SystemCaptureFailEvent,
  SystemParserWarningEvent,
  SystemSchemaDriftEvent,
  TaskMarkerEvent,
  ToolBurstEvent,
  TraceLinkEvent,
} from '../conversation-event';

interface MessageGroupItem {
  id: string;
  timestamp: string;
  body: string;
  target?: string;
  attachments?: readonly string[];
  severity?: ConversationEventSeverity;
  /** True when the rendered body is long enough that the row clamps it by default. */
  clampable: boolean;
}

interface SessionMeta {
  /** Short form (8 chars) displayed in the chip. */
  sessionIdShort?: string;
  /** Full form used in the tooltip and as the data attribute for tests. */
  sessionIdFull?: string;
  /** ISO timestamp of the captured `Session init` line, if any. */
  sessionInitAt?: string;
  /** Captured `● Rate limit ...` line text, kept verbatim for the tooltip. */
  rateLimitText?: string;
}

interface MessageGroupRow {
  kind: 'messageGroup';
  id: string;
  actor: MessageEvent['kind'];
  firstTs: string;
  lastTs: string;
  items: MessageGroupItem[];
  meta: SessionMeta;
}

type RenderRow =
  | MessageGroupRow
  | { kind: 'toolBurst'; id: string; event: ToolBurstEvent }
  | { kind: 'runMarker'; id: string; event: RunMarkerEvent }
  | { kind: 'taskMarker'; id: string; event: TaskMarkerEvent }
  | { kind: 'decision'; id: string; event: OrchestratorDecisionEvent }
  | { kind: 'supervisorWait'; id: string; event: SupervisorWaitEvent }
  | { kind: 'needsInput'; id: string; event: AgentNeedsInputEvent }
  | { kind: 'captureFail'; id: string; event: SystemCaptureFailEvent }
  | { kind: 'parserWarning'; id: string; event: SystemParserWarningEvent }
  | { kind: 'schemaDrift'; id: string; event: SystemSchemaDriftEvent }
  | { kind: 'feedbackQueued'; id: string; event: FeedbackQueuedEvent }
  | { kind: 'image'; id: string; event: ArtifactImageEvent }
  | { kind: 'tokenMetric'; id: string; event: MetricTokenEvent }
  | { kind: 'traceLink'; id: string; event: TraceLinkEvent };

/** Glyph + leading verb for the compact `feedback.queued` marker, keyed by composer mode. */
const FEEDBACK_MODE_META: Record<FeedbackQueuedEvent['mode'], { glyph: string; verb: string }> = {
  ask: { glyph: '💬', verb: 'asked' },
  defer: { glyph: '🕒', verb: 'deferred' },
  promote: { glyph: '⤴', verb: 'promoted' },
};

const MESSAGE_KINDS = new Set<MessageEvent['kind']>([
  'message.user',
  'message.taskAgent',
  'message.orchestrator',
  'message.supervisor',
  'message.supportingAgent',
]);

/**
 * Number of items rendered before the group offers a "show N more" affordance.
 * Tracks the operator's "first 3-5 items" requirement on the meta-collapse task.
 */
const VISIBLE_ITEM_LIMIT = 5;

/** Lines longer than this — or with more than two newlines — clamp to two rows. */
const ITEM_CLAMP_CHAR_LIMIT = 180;

const SESSION_INIT_RE = /^\s*●?\s*Session init\s+([0-9a-fA-F][\w-]+)/i;
const RATE_LIMIT_RE = /^\s*●?\s*Rate limit\b/i;
// `Session task_started <id>` and `Session task_notification <id> <payload>`
// arrive as plain agent text. The first capture is the session uuid; the rest
// is the payload the operator actually wants to read.
const SESSION_TASK_RE =
  /^\s*●?\s*Session\s+task_(?:started|notification)\s+([0-9a-fA-F][\w-]+)\s*([\s\S]*)$/i;

function isMessageKind(kind: ConversationEvent['kind']): kind is MessageEvent['kind'] {
  return MESSAGE_KINDS.has(kind as MessageEvent['kind']);
}

function shortenSessionId(sessionId: string): string {
  if (sessionId.length <= 8) return sessionId;
  return `${sessionId.slice(0, 8)}…`;
}

function isClampable(body: string): boolean {
  if (!body) return false;
  if (body.length > ITEM_CLAMP_CHAR_LIMIT) return true;
  let nl = 0;
  for (let i = 0; i < body.length; i++) {
    if (body.charCodeAt(i) === 10 /* \n */) nl += 1;
    if (nl > 1) return true;
  }
  return false;
}

interface ClassifiedBody {
  /** Lifecycle line that should never render as its own item. */
  meta?: 'session-init' | 'rate-limit';
  /** Optional session id captured from the body itself. */
  sessionId?: string;
  /** Payload text that should become the rendered item body. Empty string skips the item. */
  payload?: string;
  /** Verbatim text — used to populate the rate-limit tooltip. */
  raw: string;
}

function classifyMessageBody(body: string): ClassifiedBody {
  const init = SESSION_INIT_RE.exec(body);
  if (init) return { meta: 'session-init', sessionId: init[1], raw: body };
  if (RATE_LIMIT_RE.test(body)) return { meta: 'rate-limit', raw: body };
  const task = SESSION_TASK_RE.exec(body);
  if (task) {
    const payload = (task[2] ?? '').trim();
    return { sessionId: task[1], payload, raw: body };
  }
  return { raw: body };
}

/**
 * Next-gen chat conversation renderer (`Frontend:NextGenChat`).
 *
 * Pure presentational component over `ConversationEvent[]` (produced by
 * `projectConversation()`). Consecutive same-actor message events fold into
 * one bubble with a compact `<li>` list — five short agent notifications
 * become one bubble with five items instead of five framed boxes — and
 * lifecycle noise (`Session init`, `Rate limit · ...`) gets lifted into a
 * sidecar `SessionMeta` blob the bubble header surfaces on hover. The
 * coalesce rule is "same actor stays in the same box" — a USER turn or a
 * session change is the only trigger that closes the group.
 *
 * Progressive disclosure: only the first {@link VISIBLE_ITEM_LIMIT} items
 * render by default; longer groups expose a "show N more" affordance, and
 * individual items whose body would dominate the bubble clamp to two lines
 * with a per-item "expand" toggle.
 *
 * See `docs/research/embedded-chat-integration-2026-05.md` and
 * `docs/mockups/chat-window-next-gen/integration-plan.md`.
 */
@Component({
  selector: 'app-conversation-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownViewComponent, ToolBurstChipComponent, TooltipDirective],
  templateUrl: './conversation-view.component.html',
  styleUrl: './conversation-view.component.scss',
})
export class ConversationViewComponent {
  readonly events = input.required<readonly ConversationEvent[]>();
  readonly isRunning = input<boolean>(false);
  readonly variant = input<'framed' | 'embedded'>('embedded');

  readonly openTrace = output<RawLineRange | null>();
  readonly openVerboseDebug = output<void>();
  /** Raised when the user opens the queued / re-opened follow-up of a `feedback.queued` row. */
  readonly openFollowUp = output<string>();

  // Sets stay small enough that copy-on-write is fine; they only mutate on
  // user clicks ("show more", "expand"), not on every signal pass.
  private readonly expandedGroups = signal<ReadonlySet<string>>(new Set());
  private readonly expandedItems = signal<ReadonlySet<string>>(new Set());

  readonly visibleItemLimit = VISIBLE_ITEM_LIMIT;

  readonly rows = computed<RenderRow[]>(() => {
    const out: RenderRow[] = [];
    // Tuple wrapper so closures cannot narrow the cell to `never` after a
    // mutation in another closure (a TS flow-analysis quirk we hit in a
    // previous revision).
    const cell: { open: MessageGroupRow | null } = { open: null };
    let lastSeenSessionId: string | undefined;
    let lastSeenSessionInitAt: string | undefined;
    let lastSeenRateLimit: string | undefined;

    const closeGroup = (): void => {
      const open = cell.open;
      if (!open) return;
      // A group that ended up with no rendered items (only lifecycle noise
      // and zero payload) would paint an empty bubble — drop it instead so
      // the user never sees a hollow "Agent" frame.
      if (open.items.length > 0) out.push(open);
      cell.open = null;
    };

    const ensureGroup = (actor: MessageEvent['kind'], ts: string): MessageGroupRow => {
      const current = cell.open;
      if (current && current.actor === actor) return current;
      closeGroup();
      const next: MessageGroupRow = {
        kind: 'messageGroup',
        id: `group:${out.length}:${actor}:${ts}`,
        actor,
        firstTs: ts,
        lastTs: ts,
        items: [],
        meta: {
          sessionIdShort: lastSeenSessionId ? shortenSessionId(lastSeenSessionId) : undefined,
          sessionIdFull: lastSeenSessionId,
          sessionInitAt: lastSeenSessionInitAt,
          rateLimitText: lastSeenRateLimit,
        },
      };
      cell.open = next;
      return next;
    };

    for (const e of this.events()) {
      // runMarker.start is filtered: redundant with the bubble head, which
      // already says "agent active at this time". Its session id still seeds
      // the next group's dezent chip / tooltip.
      if (e.kind === 'runMarker') {
        const m = e as RunMarkerEvent;
        if (m.sessionId) {
          lastSeenSessionId = m.sessionId;
          const open = cell.open;
          if (open) {
            open.meta.sessionIdFull = m.sessionId;
            open.meta.sessionIdShort = shortenSessionId(m.sessionId);
          }
        }
        if (m.marker === 'start') continue;
        closeGroup();
        out.push({ kind: 'runMarker', id: m.id, event: m });
        continue;
      }

      if (isMessageKind(e.kind)) {
        const m = e as MessageEvent;
        const ts = m.timestamp;
        const classified = classifyMessageBody(m.body);

        if (classified.sessionId) {
          lastSeenSessionId = classified.sessionId;
          if (classified.meta === 'session-init') {
            lastSeenSessionInitAt = ts;
          }
        }
        if (classified.meta === 'rate-limit') {
          lastSeenRateLimit = classified.raw.trim();
        }

        // Lifecycle / telemetry lines never render as their own item.
        // They feed the sidecar meta the header tooltip surfaces.
        if (classified.meta === 'session-init' || classified.meta === 'rate-limit') {
          const open = cell.open;
          if (open && open.actor === m.kind) {
            // Refresh the current bubble's meta in-place so a rate-limit line
            // that arrives mid-burst still surfaces on hover.
            if (classified.sessionId) {
              open.meta.sessionIdFull = classified.sessionId;
              open.meta.sessionIdShort = shortenSessionId(classified.sessionId);
            }
            if (classified.meta === 'session-init') {
              open.meta.sessionInitAt = ts;
            } else {
              open.meta.rateLimitText = classified.raw.trim();
            }
            open.lastTs = ts;
          }
          continue;
        }

        // User messages always break the run; everything else stays glued
        // to the previous bubble of the same actor (the operator-asked
        // "same-actor-stays" rule — gap time no longer matters).
        if (m.kind === 'message.user') {
          closeGroup();
        }

        const group = ensureGroup(m.kind, ts);

        const body = classified.payload !== undefined ? classified.payload : m.body;
        // task_started with no payload is pure bookkeeping — its timing
        // information already rides on group.lastTs.
        if (!body || !body.trim()) {
          group.lastTs = ts;
          if (classified.sessionId) {
            group.meta.sessionIdFull = classified.sessionId;
            group.meta.sessionIdShort = shortenSessionId(classified.sessionId);
          }
          continue;
        }

        group.items.push({
          id: m.id,
          timestamp: ts,
          body,
          target: m.target,
          attachments: m.attachments,
          severity: m.severity,
          clampable: isClampable(body),
        });
        group.lastTs = ts;
        if (classified.sessionId) {
          group.meta.sessionIdFull = classified.sessionId;
          group.meta.sessionIdShort = shortenSessionId(classified.sessionId);
        }
        continue;
      }

      // Non-message events break the current group and dispatch to their
      // existing inline row renderer.
      closeGroup();
      switch (e.kind) {
        case 'toolBurst':
          out.push({ kind: 'toolBurst', id: e.id, event: e });
          break;
        case 'taskMarker':
          out.push({ kind: 'taskMarker', id: e.id, event: e });
          break;
        case 'decision.orchestrator':
          out.push({ kind: 'decision', id: e.id, event: e });
          break;
        case 'supervisor.wait':
          out.push({ kind: 'supervisorWait', id: e.id, event: e });
          break;
        case 'agent.needsInput':
          out.push({ kind: 'needsInput', id: e.id, event: e });
          break;
        case 'system.captureFail':
          out.push({ kind: 'captureFail', id: e.id, event: e });
          break;
        case 'system.parserWarning':
          out.push({ kind: 'parserWarning', id: e.id, event: e });
          break;
        case 'system.schemaDrift':
          out.push({ kind: 'schemaDrift', id: e.id, event: e });
          break;
        case 'feedback.queued':
          out.push({ kind: 'feedbackQueued', id: e.id, event: e });
          break;
        case 'artifact.image':
          out.push({ kind: 'image', id: e.id, event: e });
          break;
        case 'metric.token':
          out.push({ kind: 'tokenMetric', id: e.id, event: e });
          break;
        case 'traceLink':
          out.push({ kind: 'traceLink', id: e.id, event: e });
          break;
        // Workbench events fall through: existing host surfaces (run
        // timeline, screenshots strip, Verbose Debug) carry that role
        // until slice 6 lands the split presets.
        case 'workbench.summary':
        case 'workbench.gitPreview':
        case 'workbench.visualPreview':
        case 'workbench.debug':
        default:
          break;
      }
    }

    closeGroup();
    return out;
  });

  readonly hasContent = computed(() => this.rows().length > 0);

  trackByEvent = (_: number, row: RenderRow): string => row.id;

  actorLabel(kind: MessageEvent['kind']): string {
    switch (kind) {
      case 'message.user':
        return 'You';
      case 'message.taskAgent':
        return 'Agent';
      case 'message.orchestrator':
        return 'Orchestrator';
      case 'message.supervisor':
        return 'Supervisor';
      case 'message.supportingAgent':
        return 'Supporting agent';
    }
  }

  actorGlyph(kind: MessageEvent['kind']): string {
    switch (kind) {
      case 'message.user':
        return '🧑';
      case 'message.taskAgent':
        return '🤖';
      case 'message.orchestrator':
        return '🛰';
      case 'message.supervisor':
        return '🛡';
      case 'message.supportingAgent':
        return '🧰';
    }
  }

  emitOpenTrace(range?: RawLineRange | null): void {
    this.openTrace.emit(range ?? null);
  }

  emitOpenFollowUp(jobId: string | null | undefined): void {
    if (jobId) this.openFollowUp.emit(jobId);
  }

  /** Template-side handle for {@link FEEDBACK_MODE_META}. */
  readonly feedbackMeta = FEEDBACK_MODE_META;

  emitOpenVerboseDebug(): void {
    this.openVerboseDebug.emit();
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return '';
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    } catch {
      return '';
    }
  }

  formatGroupTime(group: MessageGroupRow): string {
    const first = this.formatTime(group.firstTs);
    if (group.items.length <= 1) return first;
    const last = this.formatTime(group.lastTs);
    if (!last || last === first) return first;
    return `${first}–${last}`;
  }

  formatSessionIdShort(sessionId: string | undefined): string {
    if (!sessionId) return '';
    return shortenSessionId(sessionId);
  }

  formatTokens(n: number): string {
    if (!Number.isFinite(n) || n <= 0) return '0';
    if (n < 1000) return `${n}`;
    if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`;
    return `${(n / 1_000_000).toFixed(1)}M`;
  }

  // ── Progressive disclosure ──────────────────────────────────────────

  isGroupExpanded(groupId: string): boolean {
    return this.expandedGroups().has(groupId);
  }

  toggleGroup(groupId: string): void {
    const next = new Set(this.expandedGroups());
    if (next.has(groupId)) next.delete(groupId);
    else next.add(groupId);
    this.expandedGroups.set(next);
  }

  visibleItems(group: MessageGroupRow): MessageGroupItem[] {
    if (this.isGroupExpanded(group.id)) return group.items;
    return group.items.slice(0, VISIBLE_ITEM_LIMIT);
  }

  hiddenItemCount(group: MessageGroupRow): number {
    if (this.isGroupExpanded(group.id)) return 0;
    return Math.max(0, group.items.length - VISIBLE_ITEM_LIMIT);
  }

  isItemExpanded(itemId: string): boolean {
    return this.expandedItems().has(itemId);
  }

  toggleItem(itemId: string): void {
    const next = new Set(this.expandedItems());
    if (next.has(itemId)) next.delete(itemId);
    else next.add(itemId);
    this.expandedItems.set(next);
  }

  // ── Session-meta tooltip ────────────────────────────────────────────

  hasMetaTooltip(group: MessageGroupRow): boolean {
    return !!(
      group.meta.sessionIdFull ||
      group.meta.sessionInitAt ||
      group.meta.rateLimitText
    );
  }

  metaTooltip(group: MessageGroupRow): StructuredTooltip | null {
    if (!this.hasMetaTooltip(group)) return null;
    const lines: string[] = [];
    if (group.meta.sessionIdFull) {
      lines.push(`<div><strong>Session</strong> <code>${escapeHtml(group.meta.sessionIdFull)}</code></div>`);
    }
    if (group.meta.sessionInitAt) {
      lines.push(`<div><strong>Init</strong> ${escapeHtml(this.formatTime(group.meta.sessionInitAt))}</div>`);
    }
    if (group.meta.rateLimitText) {
      lines.push(`<div><strong>Rate limit</strong> <small>${escapeHtml(group.meta.rateLimitText)}</small></div>`);
    }
    lines.push(
      `<div><small>First ${escapeHtml(this.formatTime(group.firstTs))} · Last ${escapeHtml(this.formatTime(group.lastTs))}</small></div>`
    );
    return { title: this.actorLabel(group.actor), body: lines.join('') };
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
