import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { MarkdownViewComponent } from '../../markdown-view/markdown-view.component';
import { ToolBurstChipComponent } from '../tool-burst-chip/tool-burst-chip.component';
import { TooltipDirective } from '../../tooltip';
import type {
  AgentNeedsInputEvent,
  ArtifactImageEvent,
  ConversationEvent,
  ConversationEventSeverity,
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
}

interface MessageGroupRow {
  kind: 'messageGroup';
  id: string;
  actor: MessageEvent['kind'];
  firstTs: string;
  lastTs: string;
  items: MessageGroupItem[];
  sessionId?: string;
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
  | { kind: 'image'; id: string; event: ArtifactImageEvent }
  | { kind: 'tokenMetric'; id: string; event: MetricTokenEvent }
  | { kind: 'traceLink'; id: string; event: TraceLinkEvent };

const MESSAGE_KINDS = new Set<MessageEvent['kind']>([
  'message.user',
  'message.taskAgent',
  'message.orchestrator',
  'message.supervisor',
  'message.supportingAgent',
]);

// Past 60s of silence the renderer assumes the user stepped away and starts
// a new bubble so the head time stays meaningful.
const COALESCE_GAP_MS = 60_000;

function isMessageKind(kind: ConversationEvent['kind']): kind is MessageEvent['kind'] {
  return MESSAGE_KINDS.has(kind as MessageEvent['kind']);
}

/**
 * Next-gen chat conversation renderer (`Frontend:NextGenChat`).
 *
 * Pure presentational component over `ConversationEvent[]` (produced by
 * `projectConversation()`). Consecutive same-actor message events fold into
 * one bubble with a compact `<li>` list — five short agent notifications
 * become one bubble with five items instead of five framed boxes — and
 * `runMarker.start` rows are filtered (the bubble head already communicates
 * "agent active at this time"). Session id from the preceding runMarker
 * rides along as a dezent chip in the bubble head.
 *
 * Workbench events are skipped in slice 1; existing host surfaces (run
 * timeline, screenshots strip, Verbose Debug overlay) carry that role.
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

  readonly rows = computed<RenderRow[]>(() => {
    const out: RenderRow[] = [];
    let open: MessageGroupRow | null = null;
    let lastSeenSessionId: string | undefined;

    const closeGroup = () => {
      if (open) {
        out.push(open);
        open = null;
      }
    };

    for (const e of this.events()) {
      // runMarker.start is filtered: redundant with the bubble head, which
      // already says "agent active at this time". Its session id still seeds
      // the next group's dezent chip.
      if (e.kind === 'runMarker') {
        const m = e as RunMarkerEvent;
        if (m.sessionId) lastSeenSessionId = m.sessionId;
        if (m.marker === 'start') continue;
        closeGroup();
        out.push({ kind: 'runMarker', id: m.id, event: m });
        continue;
      }

      if (isMessageKind(e.kind)) {
        const m = e as MessageEvent;
        const ts = m.timestamp;
        const tsMs = Date.parse(ts);
        const lastMs = open ? Date.parse(open.lastTs) : 0;
        const sameActor = !!open && open.actor === m.kind;
        const withinGap =
          !!open &&
          Number.isFinite(tsMs) &&
          Number.isFinite(lastMs) &&
          tsMs - lastMs < COALESCE_GAP_MS;

        if (!sameActor || !withinGap) {
          closeGroup();
          open = {
            kind: 'messageGroup',
            id: `group:${m.id}`,
            actor: m.kind,
            firstTs: ts,
            lastTs: ts,
            items: [],
            sessionId: lastSeenSessionId,
          };
        }
        open!.items.push({
          id: m.id,
          timestamp: ts,
          body: m.body,
          target: m.target,
          attachments: m.attachments,
          severity: m.severity,
        });
        open!.lastTs = ts;
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
    if (sessionId.length <= 8) return sessionId;
    return `${sessionId.slice(0, 8)}…`;
  }

  formatTokens(n: number): string {
    if (!Number.isFinite(n) || n <= 0) return '0';
    if (n < 1000) return `${n}`;
    if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`;
    return `${(n / 1_000_000).toFixed(1)}M`;
  }
}
