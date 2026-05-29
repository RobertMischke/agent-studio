import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { CliType, JobInfo } from '../../../../../models/task.model';
import type { CliModelInfo } from '../../../../cli';
import type { RunRecord } from '../../../../run-timeline';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { AgentWorkSummaryPollService } from '../../../../polling/services/agent-work-summary-poll.service';
import { ClientService } from '../../../../../services/client.service';
import { ChatModelBadgeComponent } from '../../chat-model-badge/chat-model-badge.component';
import { RegressionRadarComponent } from '../../../../regression-radar/components/regression-radar.component';
import { TooltipDirective } from '../../../../../components/tooltip';
import {
  cliTypeIcon,
  cliTypeLabel,
  formatTokens,
} from '../../../../../services/format.util';
import { projectIdentity } from '../../../../../services/project-identity.util';
import { JobService } from '../../../../../services/task.service';
import { NotificationService } from '../../../../../services/notification.service';
import { ModalStackService } from '../../../../../services/modal-stack.service';
import { copyTextToClipboard } from '../../../../../services/clipboard.util';

@Component({
  selector: 'app-overview-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatModelBadgeComponent, RegressionRadarComponent, TooltipDirective],
  templateUrl: './overview-pane.component.html',
  styleUrl: './overview-pane.component.scss',
})
export class OverviewPaneComponent {
  readonly job = input.required<JobInfo>();
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  readonly isRunning = input(false);

  readonly modelChange = output<string>();
  readonly cliTypeChange = output<CliType>();
  /** Fired after a successful title PUT so the parent can re-fetch the
   *  detail and let the optimistic override drop back to the canonical
   *  `job().title`. */
  readonly titleSaved = output<void>();

  private readonly runTimelinePoll = inject(RunTimelinePollService);
  private readonly agentWorkPoll = inject(AgentWorkSummaryPollService);
  private readonly clients = inject(ClientService);
  private readonly jobService = inject(JobService);
  private readonly notifs = inject(NotificationService);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  readonly timeline = this.runTimelinePoll.timeline;
  readonly runs = this.runTimelinePoll.runs;

  /** Title inline-edit state — local to this pane (the detail-header's edit
   *  is parent-owned and separate). Optimistic: `optimisticTitle` overrides
   *  the displayed value the moment the user hits Enter / blurs, so there
   *  is no spinner between PUT and the parent's re-fetch landing. */
  readonly editingTitle = signal(false);
  readonly titleDraft = signal('');
  readonly savingTitle = signal(false);
  private readonly optimisticTitle = signal<string | null>(null);
  private modalStackDisposer: (() => void) | null = null;

  /** Title the H1 renders. Falls back to the job id when no title is set. */
  readonly displayedTitle = computed<string>(() => {
    const opt = this.optimisticTitle();
    if (opt != null) return opt;
    return this.job().title || this.job().id;
  });

  /** Visual identity (initial + colour) of the project, for the sub-line. */
  readonly identity = computed(() => projectIdentity(this.job().projectName));

  /** Clear the optimistic override once the real `job().title` catches up
   *  to the saved value (parent re-fetched the detail after PUT). */
  private clearOptimisticOnSync = effect(() => {
    const opt = this.optimisticTitle();
    if (opt == null) return;
    const current = this.job().title || this.job().id;
    if (current === opt) {
      this.optimisticTitle.set(null);
    }
  });

  @ViewChild('titleInput') private titleInputEl?: ElementRef<HTMLInputElement>;

  private focusOnEdit = effect(() => {
    if (this.editingTitle()) {
      queueMicrotask(() => this.titleInputEl?.nativeElement.select());
    }
  });

  startTitleEdit(): void {
    if (this.editingTitle()) return;
    this.titleDraft.set(this.displayedTitle());
    this.editingTitle.set(true);
    // Push a modal-stack entry so Escape closes the edit, not the detail
    // panel. The parent's modal-stack entry only checks its own
    // editingTitle / editingPrompt signals (set by the detail-header), so
    // without this Escape would bubble past our local cancel and close
    // the whole detail view.
    this.modalStackDisposer = this.modalStack.push('overview-title-edit', () => {
      this.cancelTitleEdit();
      return true;
    });
    this.destroyRef.onDestroy(() => this.disposeModalStack());
  }

  cancelTitleEdit(): void {
    this.editingTitle.set(false);
    this.savingTitle.set(false);
    this.disposeModalStack();
  }

  saveTitle(): void {
    if (!this.editingTitle()) return;
    const trimmed = this.titleDraft().trim();
    if (!trimmed) {
      this.cancelTitleEdit();
      return;
    }
    const current = this.displayedTitle();
    if (trimmed === current) {
      this.cancelTitleEdit();
      return;
    }
    // Optimistic: paint the new title immediately, drop edit mode, fire
    // the PUT without a spinner. Revert on error.
    const job = this.job();
    this.optimisticTitle.set(trimmed);
    this.editingTitle.set(false);
    this.savingTitle.set(false);
    this.disposeModalStack();
    this.jobService.setJobTitle(job.id, trimmed, job.watchPath).subscribe({
      next: () => {
        this.titleSaved.emit();
      },
      error: () => {
        this.optimisticTitle.set(null);
        this.notifs.warning(
          'The new title could not be saved. The previous title was restored.',
          'Title save failed',
        );
      },
    });
  }

  copyTitle(): void {
    const text = this.displayedTitle();
    if (!text) return;
    copyTextToClipboard(text).then(ok => {
      if (ok) this.notifs.success('Task title copied to clipboard', 'Title copied');
    });
  }

  onTitleDraftInput(value: string): void {
    this.titleDraft.set(value);
  }

  private disposeModalStack(): void {
    if (this.modalStackDisposer) {
      this.modalStackDisposer();
      this.modalStackDisposer = null;
    }
  }

  /**
   * Derived from `logs/session-events.jsonl` + `logs/tool-calls.jsonl`.
   * Drives the Agent Work block that replaced the raw SESSION row.
   */
  readonly agentWork = this.agentWorkPoll.summary;

  readonly hasAgentWork = computed(() => {
    const s = this.agentWork();
    return s != null && (s.calls > 0 || s.toolCalls > 0);
  });

  /** Top N tool counts to render as compact chips. */
  readonly topToolCounts = computed(() => {
    const s = this.agentWork();
    if (s == null) return [];
    return s.toolCounts.slice(0, 6);
  });

  /** Comma-separated tool tooltip (full list) for the "Tools" row. */
  readonly toolCountsTooltip = computed(() => {
    const s = this.agentWork();
    if (s == null || s.toolCounts.length === 0) return '';
    return s.toolCounts.map(tc => `${tc.tool}: ${tc.count}`).join('\n');
  });

  /** Short rendering of the session id for the optional debug tooltip. */
  readonly sessionDebugTooltip = computed(() => {
    const id = this.job().sessionName;
    if (!id) return '';
    return `Session id (debug): ${id}`;
  });

  readonly owner = computed(() => {
    const ownerId = this.job().ownerClientId;
    return this.clients.resolve(ownerId);
  });

  readonly isFailedPickup = computed(() =>
    this.job().state === '3a-failed-pickup',
  );

  readonly failureInfo = computed<string | null>(() => {
    const issue = this.job().outcomeIssue;
    if (issue) return `${issue.label}: ${issue.summary}`;
    if (this.isFailedPickup()) return 'Pickup failed (see activity log for details)';
    return null;
  });

  readonly tokenSummary = computed(() => this.job().tokenSummary ?? null);

  readonly hasOrchestratorTokens = computed(() => {
    const ts = this.tokenSummary();
    return ts !== null && ts.totalTokens > 0;
  });

  /**
   * Token / request / changes counts the CLI agent itself reported in its
   * terminal footer at the end of a run. Format is unstructured strings
   * because each CLI uses a different one (e.g. Claude `~12.5k tokens`,
   * Copilot `tokens: 8,123`). Surfaced verbatim — combining with
   * `tokenSummary` (orchestrator-side, structured) would lose information.
   */
  readonly agentUsage = computed(() => {
    const lu = this.job().lastUsage;
    if (!lu) return null;
    if (!lu.tokens && !lu.requests && !lu.changes) return null;
    return lu;
  });

  /**
   * Wording for the empty state. Honest about *why* there is no data
   * depending on lane state, instead of a blanket "No token data".
   */
  readonly tokensEmptyMessage = computed(() => {
    const state = this.job().state;
    if (state === '1-preparation' || state === '1a-orchestrator-prep' || state === '2-ready') {
      return 'Run not started yet. Token activity will appear here once the agent reports usage.';
    }
    if (state === '3-progress') {
      return 'Run in progress. Token activity will appear here once the agent reports a footer.';
    }
    return 'No token activity recorded for this task. The agent did not report a CLI footer and the orchestrator made no LLM calls attributed to this job.';
  });

  readonly lastRunRecord = computed<RunRecord | null>(() => {
    const r = this.runs();
    return r.length > 0 ? r[r.length - 1] : null;
  });

  readonly recentRuns = computed(() => {
    const r = this.runs();
    return r.slice(-8);
  });

  readonly totalDuration = computed(() => {
    let total = 0;
    for (const r of this.runs()) {
      if (r.durationSeconds != null) total += r.durationSeconds;
    }
    return total;
  });

  laneLabel(state: string): string {
    switch (state) {
      case '0-backlog':              return 'Backlog';
      case '1-preparation':          return 'In Preparation';
      case '1a-orchestrator-prep':   return 'Orchestrator Prep';
      case '1b-needs-human-review':  return 'Needs Human Review';
      case '2-ready':                return 'Human Ready';
      case '3-progress':             return 'In Progress';
      case '3a-failed-pickup':       return 'Failed Pickup';
      case '4-auto-review':          return 'Auto Review';
      case '5-human-review':         return 'Human Review';
      case '6-completed':            return 'Completed';
      case '7-archive':              return 'Archive';
      default:                       return state ?? '';
    }
  }

  formatRelativeTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const diffMs = Date.now() - d.getTime();
    const minutes = Math.round(diffMs / 60_000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.round(months / 12)}y ago`;
  }

  formatAbsoluteTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }

  formatTokens(n: number): string {
    return formatTokens(n);
  }

  formatDuration(seconds: number): string {
    if (seconds < 60) return `${Math.round(seconds)}s`;
    const min = Math.floor(seconds / 60);
    const sec = Math.round(seconds % 60);
    if (min < 60) return sec > 0 ? `${min}m ${sec}s` : `${min}m`;
    const hrs = Math.floor(min / 60);
    const remMin = min % 60;
    return remMin > 0 ? `${hrs}h ${remMin}m` : `${hrs}h`;
  }

  runStatusIcon(status: string): string {
    switch (status) {
      case 'completed': return '✅';
      case 'failed':    return '❌';
      case 'cancelled': return '⚠️';
      case 'running':   return '▶️';
      default:          return '❓';
    }
  }

  runTooltip(run: RunRecord): string {
    const parts: string[] = [
      `Run #${run.index + 1} (${run.intent})`,
      `Status: ${run.status}`,
    ];
    if (run.startedAt) parts.push(`Started: ${this.formatAbsoluteTime(run.startedAt)}`);
    if (run.durationSeconds != null) parts.push(`Duration: ${this.formatDuration(run.durationSeconds)}`);
    if (run.cli) parts.push(`CLI: ${run.cli}`);
    return parts.join('\n');
  }

  cliTypeLabel(t: CliType): string {
    return cliTypeLabel(t);
  }

  cliTypeIcon(t: CliType): string {
    return cliTypeIcon(t);
  }

  phaseLabel(phase: string | null | undefined): string | null {
    if (!phase) return null;
    switch (phase) {
      case 'human-ready':              return 'Human Ready';
      case 'intake-running':           return 'Intake Running';
      case 'intake-blocked':           return 'Intake Blocked';
      case 'intake-passed':            return 'Intake Passed';
      case 'execution-running':        return 'Execution Running';
      case 'post-processing-running':  return 'Post-Processing';
      default:                         return phase;
    }
  }
}
