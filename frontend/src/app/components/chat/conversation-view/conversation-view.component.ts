import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, type SafeHtml } from '@angular/platform-browser';
import { markdownToHtml } from '../../markdown-utils';
import { ToolBurstChipComponent } from '../tool-burst-chip/tool-burst-chip.component';
import { TooltipDirective } from '../../tooltip';
import type {
  AgentNeedsInputEvent,
  ArtifactImageEvent,
  ConversationEvent,
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

type RenderRow =
  | { kind: 'message'; event: MessageEvent; bodyHtml: SafeHtml }
  | { kind: 'toolBurst'; event: ToolBurstEvent }
  | { kind: 'runMarker'; event: RunMarkerEvent }
  | { kind: 'taskMarker'; event: TaskMarkerEvent }
  | { kind: 'decision'; event: OrchestratorDecisionEvent }
  | { kind: 'supervisorWait'; event: SupervisorWaitEvent }
  | { kind: 'needsInput'; event: AgentNeedsInputEvent }
  | { kind: 'captureFail'; event: SystemCaptureFailEvent }
  | { kind: 'parserWarning'; event: SystemParserWarningEvent }
  | { kind: 'schemaDrift'; event: SystemSchemaDriftEvent }
  | { kind: 'image'; event: ArtifactImageEvent }
  | { kind: 'tokenMetric'; event: MetricTokenEvent }
  | { kind: 'traceLink'; event: TraceLinkEvent };

/**
 * Next-gen chat conversation renderer (`Frontend:NextGenChat`).
 *
 * Pure presentational component. Consumes a `ConversationEvent[]` produced by
 * `projectConversation()` and renders the v6 / v7 grammar: user/agent message
 * bubbles, `app-tool-burst-chip` for tool bursts, compact inline rows for
 * orchestrator / supervisor / agent decision events, image artefact rows,
 * compact token metric chips, slim run/task markers, and trace links.
 *
 * The component is collapsed-by-default for noisy event kinds (handled by
 * `app-tool-burst-chip` itself) and exposes two outputs so the host can:
 *
 * - `openTrace(range?)` — swap the body to the legacy `app-activity-log-view`
 *   for full raw trace inspection (raw log is never deleted).
 * - `openVerboseDebug()` — open the existing `app-verbose-debug-overlay` for
 *   dense diagnostics.
 *
 * Workbench events (`workbench.summary`, `workbench.gitPreview`,
 * `workbench.visualPreview`, `workbench.debug`) are intentionally skipped in
 * slice 1: the existing run timeline, screenshots strip, and Verbose Debug
 * overlay already own those surfaces. Workbench split presets land in slice 6.
 *
 * See `docs/research/embedded-chat-integration-2026-05.md` and
 * `docs/mockups/chat-window-next-gen/integration-plan.md`.
 */
@Component({
  selector: 'app-conversation-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, ToolBurstChipComponent, TooltipDirective],
  templateUrl: './conversation-view.component.html',
  styleUrl: './conversation-view.component.scss',
})
export class ConversationViewComponent {
  readonly events = input.required<ReadonlyArray<ConversationEvent>>();
  readonly isRunning = input<boolean>(false);
  readonly variant = input<'framed' | 'embedded'>('embedded');

  readonly openTrace = output<RawLineRange | null>();
  readonly openVerboseDebug = output<void>();

  private readonly sanitizer = inject(DomSanitizer);

  readonly rows = computed<RenderRow[]>(() => {
    const out: RenderRow[] = [];
    for (const e of this.events()) {
      switch (e.kind) {
        case 'message.user':
        case 'message.taskAgent':
        case 'message.orchestrator':
        case 'message.supervisor':
        case 'message.supportingAgent':
          out.push({
            kind: 'message',
            event: e,
            bodyHtml: this.sanitizer.bypassSecurityTrustHtml(markdownToHtml(e.body ?? '')),
          });
          break;
        case 'toolBurst':
          out.push({ kind: 'toolBurst', event: e });
          break;
        case 'runMarker':
          out.push({ kind: 'runMarker', event: e });
          break;
        case 'taskMarker':
          out.push({ kind: 'taskMarker', event: e });
          break;
        case 'decision.orchestrator':
          out.push({ kind: 'decision', event: e });
          break;
        case 'supervisor.wait':
          out.push({ kind: 'supervisorWait', event: e });
          break;
        case 'agent.needsInput':
          out.push({ kind: 'needsInput', event: e });
          break;
        case 'system.captureFail':
          out.push({ kind: 'captureFail', event: e });
          break;
        case 'system.parserWarning':
          out.push({ kind: 'parserWarning', event: e });
          break;
        case 'system.schemaDrift':
          out.push({ kind: 'schemaDrift', event: e });
          break;
        case 'artifact.image':
          out.push({ kind: 'image', event: e });
          break;
        case 'metric.token':
          out.push({ kind: 'tokenMetric', event: e });
          break;
        case 'traceLink':
          out.push({ kind: 'traceLink', event: e });
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
    return out;
  });

  readonly hasContent = computed(() => this.rows().length > 0);

  trackByEvent = (_: number, row: RenderRow): string => row.event.id;

  actorLabel(kind: MessageEvent['kind']): string {
    switch (kind) {
      case 'message.user': return 'You';
      case 'message.taskAgent': return 'Agent';
      case 'message.orchestrator': return 'Orchestrator';
      case 'message.supervisor': return 'Supervisor';
      case 'message.supportingAgent': return 'Supporting agent';
    }
  }

  actorGlyph(kind: MessageEvent['kind']): string {
    switch (kind) {
      case 'message.user': return '🧑';
      case 'message.taskAgent': return '🤖';
      case 'message.orchestrator': return '🛰';
      case 'message.supervisor': return '🛡';
      case 'message.supportingAgent': return '🧰';
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

  formatTokens(n: number): string {
    if (!Number.isFinite(n) || n <= 0) return '0';
    if (n < 1000) return `${n}`;
    if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`;
    return `${(n / 1_000_000).toFixed(1)}M`;
  }
}
