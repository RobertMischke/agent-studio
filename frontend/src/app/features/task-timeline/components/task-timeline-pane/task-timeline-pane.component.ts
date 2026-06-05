import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TooltipDirective } from '../../../../components/tooltip';
import {
  AspectFindingsListComponent,
  resolveAspectFindings,
  type AspectFinding,
} from '../../../../components/aspect-findings';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import {
  TIMELINE_KIND,
  verdictLabel,
  verdictTone,
  type CompletionLoopVerdict,
  type TaskTimelineEvent,
} from '../../models/task-timeline.model';

/**
 * Timeline tab of the task-detail prompt pane (ADR-0049 / ASS-566).
 *
 * Renders the per-task event ledger (`logs/timeline.jsonl`) as a single
 * chronological story and pins the orchestrator's latest completion-loop
 * verdict (accept / reopen / escalate) as a prominent banner at the top.
 * The point is to make the "retry-until-truly-done" loop legible: the
 * operator sees each reopen, the gap that triggered it, the retry, and the
 * final verdict without stitching together the chat log + decision journal.
 *
 * Reads {@link TaskTimelinePollService} via DI; the service instance is
 * provided once on the parent task-detail, so this pane shares the same
 * polled snapshot as the Overview attempt-cycle indicator.
 */
@Component({
  selector: 'app-task-timeline-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, AspectFindingsListComponent],
  templateUrl: './task-timeline-pane.component.html',
  styleUrl: './task-timeline-pane.component.scss',
})
export class TaskTimelinePaneComponent {
  private readonly poll = inject(TaskTimelinePollService);

  /** Raw ledger rows, oldest first (the story reads forward). */
  readonly events = this.poll.events;
  readonly completionLoop = this.poll.completionLoop;

  readonly hasEvents = computed(() => this.events().length > 0);
  readonly hasLoop = computed(() => this.completionLoop().hasActivity);

  /** "N / M" attempt counter for the banner, or "N" when budget is unknown. */
  readonly attemptLabel = computed<string | null>(() => {
    const loop = this.completionLoop();
    if (loop.attempt == null) return null;
    return loop.maxAttempts != null ? `${loop.attempt} / ${loop.maxAttempts}` : `${loop.attempt}`;
  });

  private static readonly VERDICT_KINDS = new Set<string>([
    TIMELINE_KIND.orchestratorVerdictAccepted,
    TIMELINE_KIND.qualityLoopReopened,
    TIMELINE_KIND.orchestratorEscalated,
  ]);

  /** True for the three completion-loop terminal kinds (emphasised rows). */
  isVerdictKind(kind: string): boolean {
    return TaskTimelinePaneComponent.VERDICT_KINDS.has(kind);
  }

  verdictLabel(v: CompletionLoopVerdict | null): string {
    return verdictLabel(v);
  }

  /** Tone suffix so verdict surfaces colour consistently with the Overview. */
  verdictTone(v: CompletionLoopVerdict | null): string {
    return verdictTone(v);
  }

  /** Tone for an individual event row keyed off its kind. */
  rowTone(kind: string): string {
    switch (kind) {
      case TIMELINE_KIND.orchestratorVerdictAccepted:    return 'ok';
      case TIMELINE_KIND.qualityLoopReopened:            return 'warn';
      case TIMELINE_KIND.orchestratorEscalated:          return 'danger';
      case TIMELINE_KIND.readOnlyContainmentViolation:   return 'danger';
      default:                                           return 'neutral';
    }
  }

  /** Text-only glyph per actor (no emoji in menu/structural surfaces). */
  actorGlyph(actor: string): string {
    if (actor.startsWith('human')) return '☻';
    switch (actor) {
      case 'agent':         return '⚙';
      case 'orchestrator':  return '◆';
      case 'quality-loop':  return '↻';
      case 'system':        return '·';
      default:              return '•';
    }
  }

  /** Human label for an event kind. */
  kindLabel(kind: string): string {
    switch (kind) {
      case TIMELINE_KIND.promptCreated:               return 'Prompt created';
      case TIMELINE_KIND.agentRunStarted:             return 'Run started';
      case TIMELINE_KIND.agentRunFinished:            return 'Run finished';
      case TIMELINE_KIND.preStepStarted:              return 'Pre-step started';
      case TIMELINE_KIND.preStepFinished:             return 'Pre-step finished';
      case TIMELINE_KIND.postStepStarted:             return 'Post-step started';
      case TIMELINE_KIND.postStepFinished:            return 'Post-step finished';
      case TIMELINE_KIND.orchestratorEscalated:       return 'Escalated to human';
      case TIMELINE_KIND.orchestratorSteered:         return 'Steered';
      case TIMELINE_KIND.orchestratorVerdictAccepted: return 'Verdict: accepted';
      case TIMELINE_KIND.qualityLoopReopened:         return 'Re-opened (go again)';
      case TIMELINE_KIND.humanReviewDecided:          return 'Human review decided';
      case TIMELINE_KIND.laneChanged:                 return 'Lane changed';
      case TIMELINE_KIND.mergedIn:                    return 'Merged in';
      case TIMELINE_KIND.readOnlyContainmentViolation: return 'Containment violation';
      default:                                        return kind;
    }
  }

  /** Detail rows to render under an event, minus the ones already surfaced. */
  detailEntries(event: TaskTimelineEvent): { key: string; value: string }[] {
    const d = event.details;
    if (!d) return [];
    const hidden = new Set(['gap', 'reason', 'findings', 'attempt', 'maxAttempts', 'followUpPrompt']);
    return Object.entries(d)
      .filter(([k]) => !hidden.has(k))
      .map(([key, value]) => ({ key, value }));
  }

  /**
   * The exact steering prompt the orchestrator handed the agent for this
   * reissue/continuation, when recorded (ASS-734 traceability). Rendered as a
   * collapsible "Prompt + Context" block so the operator can verify the agent
   * was told to steer the diff rather than restart, without leaving the
   * Timeline. Null when the event carries no recorded prompt.
   */
  steeringPrompt(event: TaskTimelineEvent): string | null {
    const value = event.details?.['followUpPrompt'];
    return value && value.trim().length > 0 ? value : null;
  }

  /**
   * Resolve the structured aspect findings for one of an event's reason
   * surfaces. Prefers the structured `details["findings"]` JSON; falls back
   * to parsing the legacy `**{aspect}** [{verdict}]: {reason}` blob in the
   * named detail (`gap` for reopens, `reason` for escalations). Returns []
   * when neither yields findings, in which case the template renders the
   * raw blob as plain text (unchanged behaviour for non-aspect reasons).
   */
  aspectFindings(event: TaskTimelineEvent, key: 'gap' | 'reason'): AspectFinding[] {
    const d = event.details;
    if (!d) return [];
    // The structured `findings` array only attaches to the reopen (`gap`)
    // surface; an escalation reason is a free-form sentence.
    const structured = key === 'gap' ? d['findings'] : null;
    return resolveAspectFindings(structured, d[key]);
  }

  /**
   * Inline timestamp shown next to each event. Today's events show the time
   * only (unchanged); events on any other day prefix the date so the operator
   * can tell at a glance that they aren't from the current day. The full
   * date + time always remains available via the hover tooltip
   * ({@link formatAbsoluteTime}).
   */
  formatTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const time = d.toLocaleTimeString();
    return this.isToday(d) ? time : `${d.toLocaleDateString()} ${time}`;
  }

  private isToday(d: Date): boolean {
    const now = new Date();
    return (
      d.getFullYear() === now.getFullYear() &&
      d.getMonth() === now.getMonth() &&
      d.getDate() === now.getDate()
    );
  }

  formatAbsoluteTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }
}
