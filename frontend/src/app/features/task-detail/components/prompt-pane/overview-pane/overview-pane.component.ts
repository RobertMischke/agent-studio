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
import type { CliType, PromoteToCodingResponse, TaskInfo } from '../../../../../models/task.model';
import { CreateTaskFormService, type PendingAttachment } from '../../../../board';
import type { CliModelInfo } from '../../../../cli';
import type { RunRecord } from '../../../../run-timeline';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { CompletionLoopIndicatorComponent } from '../../../../task-timeline';
import { AgentWorkSummaryPollService } from '../../../../polling/services/agent-work-summary-poll.service';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { TaskTimelinePollService } from '../../../../polling/services/task-timeline-poll.service';
import type {
  PipelineExecutionRecord,
  PipelineStep,
  PipelineStepExecution,
  PipelineStepStatus,
  StepKind,
  StepRunMode,
} from '../../../../task-pipeline';
import { ClientService } from '../../../../../services/client.service';
import { CliModelSelectorComponent } from '../../../../../components/cli-model-selector';
import { RegressionRadarComponent } from '../../../../regression-radar/components/regression-radar.component';
import { ReferencesSectionComponent } from '../../references-section/references-section.component';
import { TooltipDirective } from '../../../../../components/tooltip';
import type { StructuredTooltip } from '../../../../../components/tooltip';
import { RowComponent } from '../../../../../components/row/row.component';
import {
  cliTypeIcon,
  cliTypeLabel,
  formatTokens,
} from '../../../../../services/format.util';
import { projectIdentity } from '../../../../../services/project-identity.util';
import { TaskService } from '../../../../../services/task.service';
import { NotificationService } from '../../../../../services/notification.service';
import { ModalStackService } from '../../../../../services/modal-stack.service';
import { copyTextToClipboard } from '../../../../../services/clipboard.util';

/** One per-step row in the Overview pipeline block. */
interface PipelineRowVm {
  id: string;
  label: string;
  kind: StepKind;
  /**
   * 'parallel' for the read-only aspect reviews that run concurrently in the
   * orchestrator pool; 'sequential' for the core run and the single final
   * verdict. Drives the "Parallel" badge so the two phases read as distinct.
   */
  runMode: StepRunMode;
  enabled: boolean;
  /** Effective display status: 'disabled' for project-disabled steps. */
  status: PipelineStepStatus | 'disabled';
  model: string | null;
  verdict: string | null;
  /**
   * Structured tooltip for the verdict pill, built from the per-aspect
   * concern summary. Null unless the step flagged a concern, so a pass
   * verdict never grows a misleading tooltip.
   */
  concernTooltip: StructuredTooltip | null;
  /** Recorded wall-clock duration of the step in ms; 0 when not yet run. */
  durationMs: number;
  /** ISO start stamp from the execution record; null until the step starts. */
  startedAt: string | null;
  /** ISO end stamp; null while running or before the step is reached. */
  completedAt: string | null;
  totalTokens: number;
  costUsd: number;
  /** False -> the model is not in the price table, render cost as n/a. */
  costKnown: boolean;
}

/**
 * One row in the "Previous runs" strip below the pipeline steps. A restart
 * archives the prior run's record; this is the compact summary the operator
 * scans to tell an old run apart from the current one.
 */
interface PreviousRunVm {
  attempt: number;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number;
  passed: number;
  failed: number;
  /** Structured tooltip listing per-step outcomes for this archived run. */
  tooltip: StructuredTooltip | null;
}

/** Short unique id for a seeded create-modal attachment (mirrors the dialog's own). */
function makeAttachmentId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
  }
  return Math.random().toString(36).slice(2, 14);
}

/** Title-case label for an aspect concern verdict, for the tooltip header. */
function verdictTitle(verdict: string | null): string | null {
  switch ((verdict ?? '').toLowerCase()) {
    case 'concern':
    case 'concerns':      return 'Concerns';
    case 'blocked':
    case 'block':         return 'Blocking concern';
    // Auto-mode Ralph-loop guard verdicts (pre-loop-guard step).
    case 'looping':       return 'Loop forming';
    case 'loop-detected': return 'Loop detected';
    default:              return null;
  }
}

/**
 * Reconcile the CORE step's self-reported verdict against its deterministic
 * status so the row can never show a red "Failed" icon next to a green
 * "SUCCESS" badge (bug ASS-2). The status (icon) is authoritative — it is the
 * classified run status, not a prompt-based self-report — so a non-passed CORE
 * step that still claims a success-class outcome ('success'/'noop') has its
 * verdict dropped. The backend now writes a reconciled record, but this also
 * guards legacy on-disk records persisted before the fix, which are only
 * rewritten when the task re-runs. Every consistent pairing passes through.
 */
function reconcileCoreVerdict(
  status: PipelineRowVm['status'],
  verdict: string | null,
): string | null {
  if (status === 'passed') return verdict;
  const claim = (verdict ?? '').toLowerCase();
  if (claim === 'success' || claim === 'noop') return null;
  return verdict;
}

/**
 * Build the structured tooltip for an aspect step's verdict pill. Returns
 * null unless the step carries concern detail (a non-pass verdict with
 * summary text), so a pass verdict — or a step the backend left unenriched
 * — shows no tooltip rather than a misleading empty one.
 */
function buildConcernTooltip(
  label: string,
  verdict: string | null,
  summary: string | null,
): StructuredTooltip | null {
  const text = summary?.trim();
  if (!text) return null;
  const kind = verdictTitle(verdict);
  if (!kind) return null;
  return { title: `${label} · ${kind}`, body: text };
}

@Component({
  selector: 'app-overview-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CliModelSelectorComponent, RegressionRadarComponent, ReferencesSectionComponent, TooltipDirective, RowComponent, CompletionLoopIndicatorComponent],
  templateUrl: './overview-pane.component.html',
  styleUrl: './overview-pane.component.scss',
})
export class OverviewPaneComponent {
  readonly job = input.required<TaskInfo>();
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  readonly isRunning = input(false);
  /** Optimistic CLI + model values from the parent task-detail. The badge
   *  uses these (when set) so it re-renders synchronously after the picker
   *  fires `agentConfigCommit`, ahead of the parent's detail re-fetch.
   *  Falls back to `job().cliType` / `job().model` when the parent has not
   *  populated the override yet. Matches the chat-composer wiring at the
   *  protocol-pane, see ADR-0046. */
  readonly cliTypeOverride = input<CliType | null | undefined>(undefined);
  readonly modelOverride = input<string | null | undefined>(undefined);

  /** Atomic CLI + model commit from the unified <app-cli-model-selector>
   *  picker. The parent task-detail handler issues both PUTs in sequence. */
  readonly agentConfigCommit = output<{ cliType: CliType; model: string }>();
  /** Re-emitted from the embedded References section after a successful write. */
  readonly referencesChanged = output<void>();
  /** Fired after a successful title PUT so the parent can re-fetch the
   *  detail and let the optimistic override drop back to the canonical
   *  `job().title`. */
  readonly titleSaved = output<void>();

  private readonly runTimelinePoll = inject(RunTimelinePollService);
  private readonly agentWorkPoll = inject(AgentWorkSummaryPollService);
  private readonly pipelinePoll = inject(TaskPipelinePollService);
  private readonly timelinePoll = inject(TaskTimelinePollService);
  private readonly clients = inject(ClientService);
  private readonly jobService = inject(TaskService);
  private readonly notifs = inject(NotificationService);
  private readonly modalStack = inject(ModalStackService);
  private readonly createForm = inject(CreateTaskFormService);
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

  /** In-flight guard for the promote-to-coding fetch (payload + images). */
  readonly promoting = signal(false);

  /**
   * Lanes a planning task counts as "finished successfully" for the
   * promote affordance — it has reached review or completion, not a
   * failure / still-running lane. See
   * docs/research/planning-research-task-kinds-2026-05.md.
   */
  private static readonly FINISHED_STATES = new Set([
    '4-auto-review',
    '5-human-review',
    '6-completed',
  ]);

  /**
   * "Promote to coding task" is offered only on a planning task whose latest
   * run finished. Research tasks are read-only reports by design and never
   * show it; coding tasks have nothing to promote to.
   */
  readonly canPromote = computed(() =>
    this.job().mode === 'planning'
    && OverviewPaneComponent.FINISHED_STATES.has(this.job().state),
  );

  /**
   * Fetch the pre-fill draft for this planning task, pull each copyable image
   * down as a blob (so the create modal can re-upload it byte-for-byte), then
   * open the create-task modal seeded with that draft. The modal stays the
   * single source of truth for the create UX.
   */
  promote(): void {
    if (this.promoting() || !this.canPromote()) return;
    const job = this.job();
    this.promoting.set(true);
    this.jobService.getPromoteToCoding(job.id, job.watchPath).subscribe({
      next: (payload) => {
        void this.fetchPromoteAttachments(payload).then((attachments) => {
          this.createForm.openPromotePlanning(payload, attachments);
          this.promoting.set(false);
        });
      },
      error: () => {
        this.promoting.set(false);
        this.notifs.warning(
          'Could not prepare a coding task from this planning report. Try again in a moment.',
          'Promote failed',
        );
      },
    });
  }

  /**
   * Download each promote attachment as a File wrapped in a PendingAttachment.
   * A single failed image is skipped (the rest still come along) rather than
   * failing the whole promotion.
   */
  private async fetchPromoteAttachments(payload: PromoteToCodingResponse): Promise<PendingAttachment[]> {
    const pending: PendingAttachment[] = [];
    for (const ref of payload.attachments) {
      try {
        const res = await fetch(ref.url);
        if (!res.ok) continue;
        const blob = await res.blob();
        const file = new File([blob], ref.fileName, { type: blob.type || 'image/png' });
        pending.push({
          id: makeAttachmentId(),
          file,
          alt: ref.fileName,
          previewUrl: URL.createObjectURL(blob),
        });
      } catch {
        // Skip this image; keep the rest of the promotion intact.
      }
    }
    return pending;
  }

  /** Effective CLI + model the Agent block renders. The override wins when
   *  the parent provides one (optimistic state from the picker commit) so
   *  the badge updates without a network round-trip; otherwise we fall
   *  back to the canonical `job()` value. */
  readonly effectiveCliType = computed<CliType | null>(() => {
    const override = this.cliTypeOverride();
    return override !== undefined ? (override as CliType | null) : this.job().cliType;
  });
  readonly effectiveModel = computed<string | null>(() => {
    const override = this.modelOverride();
    return override !== undefined ? override : this.job().model;
  });

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

  readonly failureInfo = computed<string | null>(() => {
    const issue = this.job().outcomeIssue;
    if (issue) return `${issue.label}: ${issue.summary}`;
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

  /**
   * Per-step pipeline rows for the Overview pipeline block. Joins the
   * static catalogue (ordered pre+core+post, gives label/kind) with the
   * recorded execution (status/model/verdict/tokens), the derived cost,
   * and the per-project config (enabled flag + model override). Steps the
   * project disabled still render — as a struck-through "disabled" row —
   * so the operator can see what was switched off, not just what ran.
   */
  readonly pipelineRows = computed<PipelineRowVm[]>(() => {
    const res = this.pipelinePoll.pipeline();
    if (res == null) return [];
    const steps: PipelineStep[] =
      res.pipeline.allSteps ??
      [...res.pipeline.pre, ...res.pipeline.core, ...res.pipeline.post];

    const exec = new Map((res.execution?.steps ?? []).map(s => [s.stepId.toLowerCase(), s]));
    const cost = new Map((res.cost?.steps ?? []).map(c => [c.stepId.toLowerCase(), c]));

    return steps.map(step => {
      const key = step.id.toLowerCase();
      const e = exec.get(key);
      const c = cost.get(key);
      const cfg = res.config?.[step.id];
      const enabled = cfg?.enabled ?? true;
      let status: PipelineRowVm['status'];
      if (!enabled) status = 'disabled';
      else if (e) status = e.status;
      else if (step.stub) status = 'planned';
      else status = 'pending';
      const label = step.displayName || step.id;
      let verdict = e?.verdict ?? null;
      if (step.kind === 'core') verdict = reconcileCoreVerdict(status, verdict);
      return {
        id: step.id,
        label,
        kind: step.kind,
        runMode: step.runMode,
        enabled,
        status,
        model: e?.model ?? cfg?.model ?? step.model ?? null,
        verdict,
        concernTooltip: buildConcernTooltip(label, verdict, e?.verdictSummary ?? null),
        durationMs: e?.durationMs ?? 0,
        startedAt: e?.startedAt ?? null,
        completedAt: e?.completedAt ?? null,
        totalTokens: c?.totalTokens ?? 0,
        costUsd: c?.costUsd ?? 0,
        costKnown: c ? c.modelKnown : true,
      };
    });
  });

  readonly hasPipeline = computed(() => this.pipelineRows().length > 0);

  /**
   * The single core "Agent execution" row, used to surface the run count and
   * its details popover. The catalogue carries exactly one `core` step
   * (`core-agent-run`); null before the pipeline catalogue loads.
   */
  private readonly agentExecutionRow = computed<PipelineRowVm | null>(
    () => this.pipelineRows().find(r => r.kind === 'core') ?? null,
  );

  /**
   * Execution count for the core Agent-execution row. Read from the same
   * `RunTimeline.runCount` that drives the Overview "Runs" value, with the
   * Agent Work call count as a fallback, so the row can never drift from the
   * numbers shown elsewhere on the tab. 0 when no run has happened yet, which
   * keeps the row in its existing dash state.
   */
  readonly agentRunCount = computed<number>(() => {
    const tl = this.timeline();
    if (tl && tl.runCount > 0) return tl.runCount;
    return this.agentWork()?.calls ?? 0;
  });

  /** "1 run" / "N runs" label for the count badge. */
  readonly agentRunCountLabel = computed<string>(() => {
    const n = this.agentRunCount();
    return n === 1 ? '1 run' : `${n} runs`;
  });

  /**
   * Runs that had to reconstruct context after a failed session resume —
   * counted from the run-timeline intents, falling back to the Agent Work
   * `recovered` flag when the timeline is unavailable.
   */
  private readonly recoveredRunCount = computed<number>(() => {
    const recovery = this.runs().filter(r => r.intent === 'recovery').length;
    if (recovery > 0) return recovery;
    return this.agentWork()?.recovered ? 1 : 0;
  });

  /** First run start, preferring the run-timeline over the agent-work rollup. */
  private readonly agentRunFirstAt = computed<string | null>(() => {
    const tl = this.timeline();
    if (tl?.firstStartedAt) return tl.firstStartedAt;
    const runs = this.runs();
    if (runs.length > 0 && runs[0].startedAt) return runs[0].startedAt;
    return this.agentWork()?.startedAt ?? null;
  });

  /** Latest run activity, preferring the run-timeline over the agent-work rollup. */
  private readonly agentRunLastAt = computed<string | null>(() => {
    const tl = this.timeline();
    if (tl?.lastActivityAt) return tl.lastActivityAt;
    return this.agentWork()?.lastTouchAt ?? null;
  });

  /**
   * Structured popover for the Agent-execution run-count badge: run count,
   * recovered count, CLI / model / session summary, first-run and
   * last-activity stamps, plus a pointer to the Timeline tab for the full
   * story. Null when no run has happened so the badge — which is only
   * rendered for count > 0 — never carries an empty tooltip.
   */
  readonly agentRunTooltip = computed<StructuredTooltip | null>(() => {
    const count = this.agentRunCount();
    if (count <= 0) return null;
    const lines: string[] = [`Runs: ${count}`];
    const recovered = this.recoveredRunCount();
    if (recovered > 0) lines.push(`Recovered: ${recovered}`);
    const cli = this.effectiveCliType();
    if (cli) lines.push(`CLI: ${this.cliTypeLabel(cli)}`);
    const model = this.agentExecutionRow()?.model ?? this.effectiveModel();
    if (model) lines.push(`Model: ${model}`);
    const session = this.job().sessionName ?? this.agentWork()?.currentSessionId ?? null;
    if (session) lines.push(`Session: ${session}`);
    const first = this.agentRunFirstAt();
    if (first) lines.push(`First run: ${this.formatAbsoluteTime(first)}`);
    const last = this.agentRunLastAt();
    if (last) lines.push(`Last activity: ${this.formatAbsoluteTime(last)}`);
    lines.push('See the Timeline tab for the full run history.');
    return {
      title: count === 1 ? 'Agent execution · 1 run' : `Agent execution · ${count} runs`,
      body: lines.join('\n'),
    };
  });

  /**
   * True once the completion loop has produced at least one verdict. Read
   * from the shared timeline poll (same instance the consolidated
   * completion-loop strip binds to) so the Pipeline section can render even
   * when only loop activity — and no pipeline execution — exists yet.
   */
  readonly hasCompletionLoop = computed(() => this.timelinePoll.completionLoop().hasActivity);

  /** Render the Pipeline section when there are steps or completion-loop activity. */
  readonly hasPipelineSection = computed(() => this.hasPipeline() || this.hasCompletionLoop());

  /** Task-total tokens + cost across all recorded steps. */
  readonly pipelineTotal = computed(() => {
    const c = this.pipelinePoll.pipeline()?.cost ?? null;
    if (c == null) return null;
    return { totalTokens: c.totalTokens, totalCostUsd: c.totalCostUsd, anyModelUnknown: c.anyModelUnknown };
  });

  /** True once at least one step has a recorded execution. */
  readonly hasPipelineExecution = computed(() => this.pipelinePoll.hasExecution());

  /** The current run's execution record, or null before any run. */
  private readonly pipelineExecution = computed<PipelineExecutionRecord | null>(
    () => this.pipelinePoll.pipeline()?.execution ?? null,
  );

  /** 1-based run counter for the current pipeline run (1 when never restarted). */
  readonly pipelineAttempt = computed<number>(() => this.pipelineExecution()?.attempt ?? 1);

  /**
   * True when this job's pipeline has been restarted at least once, so the
   * Overview can flag the current run as a fresh attempt and surface the
   * archived prior runs. A restart shows up as attempt > 1 or as a non-empty
   * archive (belt-and-suspenders in case only one of the two is populated).
   */
  readonly isPipelineRestart = computed<boolean>(() => {
    const exec = this.pipelineExecution();
    if (exec == null) return false;
    return (exec.attempt ?? 1) > 1 || (exec.previousAttempts?.length ?? 0) > 0;
  });

  /** ISO start stamp of the current run, for the restart badge tooltip. */
  readonly pipelineStartedAt = computed<string | null>(
    () => this.pipelineExecution()?.startedAt ?? null,
  );

  /**
   * Compact summaries of prior runs, most-recent first, so an operator can
   * tell old step runs apart from the current ones after a restart. Empty
   * when the pipeline never restarted.
   */
  readonly previousRuns = computed<PreviousRunVm[]>(() => {
    const prior = this.pipelineExecution()?.previousAttempts ?? [];
    return prior.map(rec => this.toPreviousRunVm(rec));
  });

  private toPreviousRunVm(rec: PipelineExecutionRecord): PreviousRunVm {
    const steps = rec.steps ?? [];
    const passed = steps.filter(s => s.status === 'passed').length;
    const failed = steps.filter(s => s.status === 'failed').length;
    const durationMs = this.recordDurationMs(rec);
    return {
      attempt: rec.attempt ?? 1,
      startedAt: rec.startedAt ?? null,
      completedAt: rec.completedAt ?? null,
      durationMs,
      passed,
      failed,
      tooltip: this.buildPreviousRunTooltip(rec, steps, durationMs),
    };
  }

  /** Wall-clock duration of an archived run from its start/complete stamps. */
  private recordDurationMs(rec: PipelineExecutionRecord): number {
    if (!rec.startedAt || !rec.completedAt) return 0;
    const start = new Date(rec.startedAt).getTime();
    const end = new Date(rec.completedAt).getTime();
    if (Number.isNaN(start) || Number.isNaN(end)) return 0;
    return Math.max(0, end - start);
  }

  private buildPreviousRunTooltip(
    rec: PipelineExecutionRecord,
    steps: PipelineStepExecution[],
    durationMs: number,
  ): StructuredTooltip | null {
    const lines: string[] = [];
    if (rec.startedAt) lines.push(`Started: ${this.formatAbsoluteTime(rec.startedAt)}`);
    if (durationMs > 0) lines.push(`Duration: ${this.formatStepDuration(durationMs)}`);
    const ran = steps.filter(s =>
      s.status === 'passed' || s.status === 'failed' || s.status === 'skipped',
    );
    for (const s of ran) {
      lines.push(`${this.stepStatusIcon(s.status)} ${s.stepId}`);
    }
    if (lines.length === 0) return null;
    return { title: `Run #${rec.attempt ?? 1}`, body: lines.join('\n') };
  }

  /** True while any pipeline step is in flight. */
  private readonly anyStepRunning = computed(() =>
    this.pipelineRows().some(r => r.status === 'running'),
  );

  /**
   * Wall-clock "now", advanced once per second only while a step is running,
   * so the active step's duration counts up live between the 10 s pipeline
   * polls. Idle (no interval, no change detection) when nothing is running.
   * Deliberately not read by `pipelineRows` / `anyStepRunning` so ticking
   * the clock never re-triggers the interval-management effect below.
   */
  private readonly now = signal(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;

  private readonly manageLiveTick = effect(() => {
    if (this.anyStepRunning()) {
      if (this.tickHandle == null) {
        this.now.set(Date.now());
        this.tickHandle = setInterval(() => this.now.set(Date.now()), 1000);
      }
    } else if (this.tickHandle != null) {
      clearInterval(this.tickHandle);
      this.tickHandle = null;
    }
  });

  constructor() {
    this.destroyRef.onDestroy(() => {
      if (this.tickHandle != null) {
        clearInterval(this.tickHandle);
        this.tickHandle = null;
      }
    });
  }

  /**
   * Effective duration in ms for a step row: a live "now − startedAt" while
   * the step is running (so the cell ticks up), otherwise the recorded
   * `durationMs`. Reads the `now` signal so the running row re-renders each
   * second; a completed row is independent of the clock.
   */
  liveStepDurationMs(row: PipelineRowVm): number {
    if (row.status === 'running' && row.startedAt) {
      const start = new Date(row.startedAt).getTime();
      if (!Number.isNaN(start)) return Math.max(0, this.now() - start);
    }
    return row.durationMs;
  }

  /** Wall-clock "HH:MM" for a step timestamp; empty string when unset. */
  formatClock(iso: string | null): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  /**
   * Structured tooltip for a step's timing cell: absolute start (and end, or
   * a live "running for" line). Null before the step starts so a pending /
   * disabled row carries no misleading tooltip.
   */
  stepTimingTooltip(row: PipelineRowVm): StructuredTooltip | null {
    if (!row.startedAt) return null;
    const lines: string[] = [`Started: ${this.formatAbsoluteTime(row.startedAt)}`];
    if (row.status === 'running') {
      lines.push(`Running for ${this.formatStepDuration(this.liveStepDurationMs(row))}`);
    } else {
      if (row.completedAt) lines.push(`Ended: ${this.formatAbsoluteTime(row.completedAt)}`);
      if (row.durationMs > 0) lines.push(`Duration: ${this.formatStepDuration(row.durationMs)}`);
    }
    return { title: row.label, body: lines.join('\n') };
  }

  stepKindLabel(kind: StepKind): string {
    switch (kind) {
      case 'module':       return 'Pre';
      case 'core':         return 'Core';
      case 'aspect':       return 'Aspect';
      case 'orchestrator': return 'Decision';
      case 'tool':         return 'Tool';
      default:             return kind;
    }
  }

  stepStatusIcon(status: PipelineRowVm['status']): string {
    switch (status) {
      case 'passed':   return '✅';
      case 'failed':   return '❌';
      case 'running':  return '▶️';
      case 'skipped':  return '⏭️';
      case 'planned':  return '🕓';
      case 'disabled': return '🚫';
      default:         return '·';
    }
  }

  stepStatusLabel(status: PipelineRowVm['status']): string {
    switch (status) {
      case 'passed':   return 'Passed';
      case 'failed':   return 'Failed';
      case 'running':  return 'Running';
      case 'skipped':  return 'Skipped';
      case 'planned':  return 'Planned';
      case 'disabled': return 'Disabled';
      default:         return 'Pending';
    }
  }

  /** USD formatting: sub-cent costs need more than 2 dp to be non-zero. */
  formatCost(usd: number): string {
    if (usd <= 0) return '$0.00';
    if (usd < 0.01) return `$${usd.toFixed(4)}`;
    return `$${usd.toFixed(2)}`;
  }

  laneLabel(state: string): string {
    switch (state) {
      case '0-backlog':              return 'Backlog';
      case '1-preparation':          return 'In Preparation';
      case '1a-orchestrator-prep':   return 'Orchestrator Prep';
      case '1b-needs-human-review':  return 'Needs Human Review';
      case '2-ready':                return 'Ready';
      case '3-progress':             return 'In Progress';
      case '4-auto-review':          return 'Auto Review';
      case '5-human-review':         return 'Review';
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

  /**
   * Per-step duration for the pipeline rows. Sub-second steps (most
   * deterministic Tool steps) show in ms; longer steps fall through to the
   * coarser m/s/h formatter. Returns an em-dash when nothing ran yet.
   */
  formatStepDuration(ms: number): string {
    if (ms <= 0) return '—';
    if (ms < 1000) return `${Math.round(ms)}ms`;
    return this.formatDuration(ms / 1000);
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
      case 'human-ready':              return 'Ready';
      case 'intake-running':           return 'Intake Running';
      case 'intake-blocked':           return 'Intake Blocked';
      case 'intake-passed':            return 'Intake Passed';
      case 'execution-running':        return 'Execution Running';
      case 'post-processing-running':  return 'Post-Processing';
      default:                         return phase;
    }
  }
}
