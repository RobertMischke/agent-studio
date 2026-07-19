import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';

import { TaskState } from '../../../../models/task.model';
import type { CliOutputLine, TaskDetail, TaskInfo } from '../../../../models/task.model';
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
 * Decision-ready escalation summary (AGT-2019): open gates, review verdict,
 * delivery context, recommendation and the system give-up reason. It aggregates
 * existing detail, chat, timeline and review artifacts without mutating them.
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
  /** Existing task conversation/output signal owned by the detail host. */
  readonly cliOutput = input<readonly CliOutputLine[]>([]);

  private readonly jobs = inject(TaskService);
  private readonly timelinePoll = inject(TaskTimelinePollService);

  /** Newest-first code-review list for the open job; drives the verdict head. */
  private readonly codeReviews = signal<CodeReviewListEntry[]>([]);
  /** Body of `orchestrator-follow-up.md`, or null when the file is absent. */
  private readonly followUpMarkdown = signal<string | null>(null);
  /** Which job the current fetch results belong to, to drop stale responses. */
  private fetchedJobId: string | null = null;

  /** Per-task collapse state; explicit operator choice wins over defaults. */
  readonly collapsed = signal<boolean>(false);
  /** Job the current collapse state was seeded for, to re-seed on task change. */
  private collapseJobId: string | null = null;

  constructor() {
    // Seed the collapse state whenever the open task changes: the stored
    // per-task preference wins; absent that, the lane picks the default.
    effect(() => {
      const info = this.detail().info;
      if (info.id === this.collapseJobId) return;
      this.collapseJobId = info.id;
      this.collapsed.set(initialCollapsed(info));
    });

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

    // A system/infra give-up in 5-human-review is an acute hand-off, not quiet
    // history: open its reason as soon as the existing orchestrator chat line is
    // available. An operator's explicit per-task collapse preference still wins.
    effect(() => {
      const info = this.detail().info;
      if (this.view().escalation?.kind !== 'gave-up') return;
      if (readCollapsePref(info.id) !== null) return;
      this.collapsed.set(false);
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
      statusMarkdown: this.detail().statusMarkdown,
      cliOutput: this.cliOutput(),
      timeline: this.timelinePoll.events(),
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

  /**
   * DtC step 6 — header title. A GaveUpToHuman escalation says so plainly
   * ("Orchestrator gave up"), reading distinctly from a logical / quality
   * escalation ("Escalation") a human judges on its merits.
   */
  readonly headTitle = computed<string>(() =>
    this.view().escalation?.kind === 'gave-up' ? 'Orchestrator gave up' : 'Escalation',
  );

  /** Toggle the panel open/closed and persist the choice for this task. */
  toggleCollapsed(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    writeCollapsePref(this.collapseJobId, next);
  }

  formatRunAt(iso: string | null): string {
    return iso ? formatDateTimeUtc(iso) : '';
  }
}

/** localStorage key holding the per-task collapse map (`{ [jobId]: boolean }`). */
const COLLAPSE_KEY = 'taskboard.escalation.collapsed';

/**
 * Initial collapse for a freshly-opened task: an explicit stored preference
 * wins; otherwise the lane decides. The give-up effect above additionally opens
 * a system/infra hand-off once its orchestrator category is available.
 */
function initialCollapsed(info: TaskInfo): boolean {
  const stored = readCollapsePref(info.id);
  if (stored !== null) return stored;
  return info.state !== TaskState.Escalated;
}

function readCollapseMap(): Record<string, boolean> {
  try {
    const raw = localStorage.getItem(COLLAPSE_KEY);
    if (!raw) return {};
    const parsed: unknown = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, boolean>) : {};
  } catch {
    return {};
  }
}

/** Stored collapse preference for a job, or null when the operator never set one. */
function readCollapsePref(jobId: string): boolean | null {
  const value = readCollapseMap()[jobId];
  return typeof value === 'boolean' ? value : null;
}

function writeCollapsePref(jobId: string | null, value: boolean): void {
  if (!jobId) return;
  try {
    const map = readCollapseMap();
    map[jobId] = value;
    localStorage.setItem(COLLAPSE_KEY, JSON.stringify(map));
  } catch {
    /* ignore quota / privacy-mode errors — collapse still works in-memory */
  }
}
