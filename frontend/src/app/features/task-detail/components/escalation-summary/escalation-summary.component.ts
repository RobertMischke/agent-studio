import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';

import type { TaskDetail } from '../../../../models/task.model';
import { CodeReviewListEntry, TaskService } from '../../../../services/task.service';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import {
  isSteeringKind,
  steeringInfoFromEvent,
  type SteeringInfo,
} from '../../../../components/steering-detail';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatDateTimeUtc } from '../../../../services/format.util';
import {
  buildEscalationSummaryView,
  type EscalationGateSource,
  type EscalationSummaryView,
} from './escalation-summary.util';

/**
 * Escalation summary panel (AGT-2019). Renders — prominently, above the panes —
 * for a `5e-escalated` card the four things an operator needs to make the call
 * that the thin last-run status protocol never showed: the open gate points,
 * the code-review verdict, the delivery context (already in develop?), and the
 * gate's recommendation. Pure display aggregation of existing artifacts; see
 * {@link buildEscalationSummaryView} for the source-priority rules.
 *
 * The panel owns two cheap fetches (the code-review grade list and the reissue
 * follow-up file) and reads the already-polled task timeline for the steering
 * event; everything else comes off `detail()`. It is otherwise presentational —
 * no mutations, no outputs.
 */
@Component({
  selector: 'app-escalation-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './escalation-summary.component.html',
  styleUrl: './escalation-summary.component.scss',
})
export class EscalationSummaryComponent {
  readonly detail = input.required<TaskDetail>();

  private readonly jobs = inject(TaskService);
  private readonly timelinePoll = inject(TaskTimelinePollService);

  /** Newest-first code-review list for the open job; drives the verdict head. */
  private readonly codeReviews = signal<CodeReviewListEntry[]>([]);
  /** Body of `orchestrator-follow-up.md`, or null when the file is absent. */
  private readonly followUpMarkdown = signal<string | null>(null);
  /** Which job the current fetch results belong to, to drop stale responses. */
  private fetchedJobId: string | null = null;

  constructor() {
    // Re-fetch the grade list + follow-up file whenever the open job changes.
    // Both are best-effort: a missing follow-up file (the common pure-escalation
    // case) or an empty review list simply collapses that section.
    effect(() => {
      const info = this.detail().info;
      if (info.id === this.fetchedJobId) return;
      this.fetchedJobId = info.id;
      this.codeReviews.set([]);
      this.followUpMarkdown.set(null);

      this.jobs.listCodeReviews(info.id, info.watchPath).subscribe({
        next: (resp) => {
          if (this.fetchedJobId !== info.id) return;
          this.codeReviews.set(resp.entries ?? []);
        },
        error: () => {
          /* leave empty; the verdict head hides when there is nothing to show */
        },
      });

      this.jobs.readJobFile(info.id, 'orchestrator-follow-up.md', info.watchPath).subscribe({
        next: (text) => {
          if (this.fetchedJobId !== info.id) return;
          this.followUpMarkdown.set(text ?? null);
        },
        error: () => {
          if (this.fetchedJobId !== info.id) return;
          // 404 is expected on a pure-escalation card — the follow-up file is a
          // reissue artifact. Fall back to the timeline / evidence sources.
          this.followUpMarkdown.set(null);
        },
      });
    });
  }

  /**
   * Latest steering step from the already-polled task timeline. Mirrors the
   * Overview pane's derivation so the escalation reason + structured gate
   * findings read the same everywhere. Null until a steering event exists.
   */
  private readonly steering = computed<SteeringInfo | null>(() => {
    const events = this.timelinePoll.events();
    for (let i = events.length - 1; i >= 0; i--) {
      if (isSteeringKind(events[i].kind)) return steeringInfoFromEvent(events[i]);
    }
    return null;
  });

  /** The aggregated view model the template renders. */
  readonly view = computed<EscalationSummaryView>(() =>
    buildEscalationSummaryView({
      info: this.detail().info,
      reviewEvidence: this.detail().reviewEvidence ?? [],
      codeReviews: this.codeReviews(),
      followUpMarkdown: this.followUpMarkdown(),
      steering: this.steering(),
    }),
  );

  /** Human label for where the gate items were sourced from. */
  gateSourceLabel(source: EscalationGateSource): string {
    switch (source) {
      case 'follow-up':
        return 'From the reissue follow-up checklist';
      case 'gate-evidence':
        return 'From the completion-gate findings';
      case 'review-evidence':
        return 'From the recorded review evidence';
      default:
        return '';
    }
  }

  /** Count of still-open (unchecked) gate items, for the section header. */
  readonly openGateCount = computed<number>(
    () => this.view().gateItems.filter((i) => !i.checked).length,
  );

  formatRunAt(iso: string | null): string {
    return iso ? formatDateTimeUtc(iso) : '';
  }
}
