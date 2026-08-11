import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  AspectFindingsListComponent,
  resolveAspectFindings,
  type AspectFinding,
} from '../../../../components/aspect-findings';
import { SteeringDetailComponent } from '../../../../components/steering-detail/steering-detail.component';
import {
  isSteeringKind,
  steeringInfoFromEvent,
  type SteeringInfo,
} from '../../../../components/steering-detail/steering-detail.model';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import { RunTimelinePollService } from '../../../polling/services/run-timeline-poll.service';
import type { RunRecord } from '../../../run-timeline';
import {
  TIMELINE_KIND,
  timelineEventIdentity,
  verdictLabel,
  verdictTone,
  type CompletionLoopVerdict,
  type TaskTimelineEvent,
} from '../../models/task-timeline.model';
import {
  timelineDetailEntries,
  timelineEventReason,
  timelineEventSummary,
  timelineEventTitle,
  timelineKindLabel,
} from '../task-timeline-presentation';
import { ProgressiveTimelineWindow } from './progressive-timeline-window';
import { timelineExecutionPresentation } from './task-timeline-execution-presentation';

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
  imports: [TooltipDirective, AspectFindingsListComponent, SteeringDetailComponent],
  templateUrl: './task-timeline-pane.component.html',
  styleUrl: './task-timeline-pane.component.scss',
})
export class TaskTimelinePaneComponent {
  private readonly poll = inject(TaskTimelinePollService);
  private readonly runPoll = inject(RunTimelinePollService, { optional: true });
  private readonly eventWindow = new ProgressiveTimelineWindow<TaskTimelineEvent>(timelineEventIdentity);
  private readonly executionPresentationCache = new WeakMap<
    TaskTimelineEvent,
    {
      runs: readonly RunRecord[];
      value: ReturnType<typeof timelineExecutionPresentation>;
    }
  >();

  /** Raw ledger rows, oldest first (the story reads forward). */
  readonly events = this.poll.events;
  readonly completionLoop = this.poll.completionLoop;
  readonly runs = computed(() => this.runPoll?.runs() ?? []);

  /**
   * Chronological story rows for the tab. New tasks should already carry
   * agent_run_* rows in timeline.jsonl. Legacy / recovered original cards
   * can predate those mirrors, so the Timeline tab synthesizes one compact
   * run-summary row per recorded RunRecord instead of showing an empty story
   * while the Protocol pane has run data.
   */
  readonly displayEvents = computed<TaskTimelineEvent[]>(() => {
    const events = this.events();
    if (events.some(e => e.kind === TIMELINE_KIND.agentRunStarted || e.kind === TIMELINE_KIND.agentRunFinished)) {
      return events;
    }
    const synthesized = this.runs().map(r => this.runSummaryEvent(r));
    return [...events, ...synthesized].sort((a, b) => this.compareIso(a.ts, b.ts));
  });

  /**
   * Variable-height timeline rows cannot use fixed-size virtual scroll safely.
   * Keep the newest page mounted and progressively prepend older pages on
   * demand. This bounds DOM and change-detection work without clipping large,
   * expanded markdown or payload rows.
   */
  readonly visibleEvents = computed(() => this.eventWindow.visible(this.displayEvents()));
  readonly olderEventCount = computed(() => this.eventWindow.olderCount(this.displayEvents().length));
  readonly olderPageSize = computed(() => this.eventWindow.nextPageSize(this.displayEvents().length));
  private readonly expandedDisclosures = signal<ReadonlySet<string>>(new Set());

  constructor() {
    effect(() => this.eventWindow.sync(this.displayEvents()));
  }

  readonly hasEvents = computed(() => this.displayEvents().length > 0);
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

  /**
   * True when this event is a steering step (accept / reissue / escalate /
   * continuation). Such rows render the shared structured steering block
   * (verdict + reason + collapsible prompt + context) instead of the ad-hoc
   * gap/reason/details rendering used for ordinary lifecycle rows.
   */
  isSteeringEvent(kind: string): boolean {
    return isSteeringKind(kind);
  }

  isExecutionContext(kind: string): boolean {
    return kind === TIMELINE_KIND.executionContext;
  }

  /** Project a steering event into the shared {@link SteeringInfo} block. */
  steeringInfo(event: TaskTimelineEvent): SteeringInfo | null {
    return steeringInfoFromEvent(event);
  }

  verdictLabel(v: CompletionLoopVerdict | null): string {
    return verdictLabel(v);
  }

  /** Tone suffix so verdict surfaces colour consistently with the Overview. */
  verdictTone(v: CompletionLoopVerdict | null): string {
    return verdictTone(v);
  }

  /** Tone for an individual event row, including pipeline terminal semantics. */
  rowTone(eventOrKind: TaskTimelineEvent | string): string {
    const kind = typeof eventOrKind === 'string' ? eventOrKind : eventOrKind.kind;
    const status = typeof eventOrKind === 'string'
      ? null
      : eventOrKind.details?.['status']?.trim().toLowerCase();
    const reason = typeof eventOrKind === 'string'
      ? null
      : eventOrKind.details?.['reason']?.trim().toLowerCase();
    const isBuildTestGate = typeof eventOrKind !== 'string'
      && (eventOrKind.details?.['step']?.trim().toLowerCase() === 'post-build-test-gate'
        || /^build\/test gate\b/i.test(eventOrKind.summary.trim()));
    if (isBuildTestGate
        && (kind === TIMELINE_KIND.preStepFinished || kind === TIMELINE_KIND.postStepFinished)
        && status === 'skipped' && reason === 'no verify commands derivable') return 'neutral';
    if (isBuildTestGate
        && (kind === TIMELINE_KIND.preStepFinished || kind === TIMELINE_KIND.postStepFinished)
        && status === 'skipped') return 'danger';
    if (isBuildTestGate
        && (kind === TIMELINE_KIND.preStepFinished || kind === TIMELINE_KIND.postStepFinished)
        && (status === 'notapplicable' || status === 'not-applicable')) return 'neutral';
    switch (kind) {
      case TIMELINE_KIND.orchestratorVerdictAccepted:    return 'ok';
      case TIMELINE_KIND.externalCompletion:             return 'ok';
      // AGT-2220: a refused stamp is a finding, not a success.
      case TIMELINE_KIND.deliveryUnverified:             return 'danger';
      case TIMELINE_KIND.steerTimeoutResolved:           return 'neutral';
      case TIMELINE_KIND.qualityLoopReopened:            return 'warn';
      case TIMELINE_KIND.orchestratorEscalated:          return 'danger';
      case TIMELINE_KIND.readOnlyContainmentViolation:   return 'danger';
      case TIMELINE_KIND.quotaFallbackActivated:         return 'warn';
      case TIMELINE_KIND.loadThrottleDecision:           return 'warn';
      case TIMELINE_KIND.integrationPendingWarning:      return 'warn';
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
    return timelineKindLabel(kind);
  }

  eventTitle(event: TaskTimelineEvent): string {
    return timelineEventTitle(event);
  }

  eventSummary(event: TaskTimelineEvent): string | null {
    return timelineEventSummary(event);
  }

  eventReason(event: TaskTimelineEvent): string | null {
    return timelineEventReason(event);
  }

  eventIdentity(event: TaskTimelineEvent): string {
    return timelineEventIdentity(event);
  }

  /** Detail rows to render under an event, minus the ones already surfaced. */
  detailEntries(event: TaskTimelineEvent) {
    return timelineDetailEntries(event);
  }

  executionFacts(event: TaskTimelineEvent): { label: string; value: string }[] {
    return this.executionPresentation(event).facts;
  }

  executionSources(event: TaskTimelineEvent) {
    return this.executionPresentation(event).sources;
  }

  loadOlder(): void {
    this.eventWindow.loadOlder(this.displayEvents().length);
  }

  isDisclosureExpanded(event: TaskTimelineEvent, disclosure: 'details' | 'sources'): boolean {
    return this.expandedDisclosures().has(this.disclosureKey(event, disclosure));
  }

  toggleDisclosure(event: TaskTimelineEvent, disclosure: 'details' | 'sources'): void {
    const key = this.disclosureKey(event, disclosure);
    this.expandedDisclosures.update(current => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
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

  private runSummaryEvent(run: RunRecord): TaskTimelineEvent {
    const status = this.runStatusLabel(run.status);
    const details: Record<string, string> = {
      run: `#${run.index}`,
      intent: run.intent || 'run',
      status,
    };
    if (run.cli) details['cli'] = run.cli;
    if (run.durationSeconds != null) details['durationSeconds'] = String(run.durationSeconds);
    if (run.exitCode != null) details['exitCode'] = String(run.exitCode);
    if (run.userFollowup) details['userFollowup'] = run.userFollowup;
    if (run.reason) details['reason'] = run.reason;

    return {
      ts: run.endedAt ?? run.startedAt,
      kind: TIMELINE_KIND.agentRunFinished,
      actor: 'agent',
      runId: run.capturedSessionId ?? run.inputSessionId ?? `run-${run.index}`,
      summary: '',
      details,
    };
  }

  private runStatusLabel(status: string): string {
    switch (status) {
      case 'completed': return 'completed';
      case 'failed': return 'failed';
      case 'running': return 'running';
      case 'stopped':
      case 'cancelled': return 'stopped';
      default: return status || 'unknown';
    }
  }

  private compareIso(a: string, b: string): number {
    const ta = new Date(a).getTime();
    const tb = new Date(b).getTime();
    if (Number.isNaN(ta) && Number.isNaN(tb)) return 0;
    if (Number.isNaN(ta)) return 1;
    if (Number.isNaN(tb)) return -1;
    return ta - tb;
  }

  private disclosureKey(event: TaskTimelineEvent, disclosure: 'details' | 'sources'): string {
    return `${timelineEventIdentity(event)}:${disclosure}`;
  }

  private executionPresentation(event: TaskTimelineEvent) {
    const runs = this.runs();
    const cached = this.executionPresentationCache.get(event);
    if (cached?.runs === runs) return cached.value;
    const presentation = timelineExecutionPresentation(event, runs);
    this.executionPresentationCache.set(event, { runs, value: presentation });
    return presentation;
  }
}
