import { ChangeDetectionStrategy, Component, DestroyRef, computed, HostListener, inject, input, output, signal, effect, OnDestroy, ViewChild, ViewEncapsulation } from '@angular/core';
import { ModalStackService } from '../../services/modal-stack.service';
import { FormsModule } from '@angular/forms';
import type { JobDetail, JobInfo, WatchPathEntry, CliSettings, CliType, ContinueMode } from '../../models/job.model';
import { CLI_TYPES } from '../../models/job.model';
import type { CliModelInfo } from '../../features/cli';
import { JobService } from '../../services/job.service';
import { ErrorDialogService } from '../../services/error-dialog.service';
import { NowTickService } from '../../services/now-tick.service';
import {
  formatTokens as fmtTokens,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn,
  stateLabel as fmtStateLabel,
  formatTime as fmtTime,
  formatDate as fmtDate,
  formatDateTime as fmtDateTime,
  formatMultiplier as fmtMultiplier,
  cliTypeLabel as fmtCliTypeLabel
} from '../../services/format.util';
import { LayoutPanesService } from './services/layout-panes.service';
import { LanePagerService } from './state/lane-pager.service';
import { ClaudeSessionPollService } from '../polling/services/claude-session-poll.service';
import { SessionEventsPollService } from '../polling/services/session-events-poll.service';
import { RunTimelinePollService } from '../polling/services/run-timeline-poll.service';
import { ScreenshotsPollService } from '../polling/services/screenshots-poll.service';
import { GitPaneService } from './services/git-pane.service';
import { shouldShowFailureToast } from './services/run-outcome.util';
import { GitPaneComponent } from './components/git-pane/git-pane.component';
import { CliOutputPollService } from '../polling/services/cli-output-poll.service';
import { CommandDeckComponent } from './components/command-deck/command-deck.component';
import { PromptPaneComponent } from './components/prompt-pane/prompt-pane.component';
import { LogOverlayComponent } from './components/log-overlay/log-overlay.component';
import { ProtocolPaneComponent } from './components/protocol-pane/protocol-pane.component';
import { DetailHeaderComponent } from './components/detail-header/detail-header.component';
import { CliConfigCardComponent } from './components/cli-config-card/cli-config-card.component';
import { PaneToggleBarComponent } from './components/pane-toggle-bar/pane-toggle-bar.component';
import { TriagePanelComponent, TriageActionPayload } from './components/triage-panel/triage-panel.component';
import { markdownToHtml } from '../../components/markdown-utils';

@Component({
  selector: 'app-job-detail',
  standalone: true,
  imports: [FormsModule, GitPaneComponent, CommandDeckComponent, PromptPaneComponent, LogOverlayComponent, ProtocolPaneComponent, DetailHeaderComponent, CliConfigCardComponent, PaneToggleBarComponent, TriagePanelComponent],
  providers: [LayoutPanesService, ClaudeSessionPollService, SessionEventsPollService, RunTimelinePollService, ScreenshotsPollService, GitPaneService, CliOutputPollService],
  // Cycle 7b: OnPush. The detail panel mounts seven polling services
  // (claude session, session events, run timeline, screenshots,
  // git pane, cli output, hygiene strip) plus the protocol/log/git
  // panes; each poll tick used to trigger a default-CD pass over the
  // whole subtree. Signals already mark themselves dirty, so OnPush
  // prunes the unrelated work without changing behavior.
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Keep styles global to this subtree so the still-inline class rules
  // (.pane*, .detail*, .inspector*, .notes-panel*, .sidebar-card*, …)
  // continue to reach the now-extracted sub-components without having
  // to copy each block into its own .scss. Step 9 (per-component
  // styles) can flip this back to default once all blocks have moved.
  encapsulation: ViewEncapsulation.None,
  templateUrl: './job-detail.html',
  styleUrl: './job-detail.scss'
})
export class JobDetailComponent implements OnDestroy {
  readonly detail = input.required<JobDetail>();
  readonly watchPaths = input<WatchPathEntry[]>([]);
  /** Peers in the same on-disk lane as the current job, in kanban order. */
  readonly lanePeers = input<JobInfo[]>([]);
  /** True while the update-service is mid-update; disables triage actions. */
  readonly mutationsBlocked = input(false);
  readonly back = output<void>();
  readonly fileSaved = output<void>();
  readonly projectChanged = output<string>();
  readonly completeAndNextReview = output<void>();
  readonly deleteRequested = output<void>();
  readonly stateChangeRequested = output<{ targetState: string }>();
  /** Lane-move requested via the triage panel. Parent runs the API call so
   *  it can advance to the next peer on success. */
  readonly triageMoveRequested = output<{ targetState: string; actionId: string }>();
  /** "Move to top" via the triage panel. */
  readonly triageMoveToTopRequested = output<{ actionId: string }>();
  /** "Delete" via the triage panel (already destructive-confirmed inside). */
  readonly triageDeleteRequested = output<{ actionId: string }>();
  /** "Run now" — start the CLI for a 2-ready job. Parent has runner state. */
  readonly triageStartRequested = output<{ actionId: string }>();
  /** Walk to the next peer in the current lane (j / ↓ / → / Next button). */
  readonly nextInLaneRequested = output<void>();
  /** Walk to the previous peer in the current lane (k / ↑ / ← / Prev button). */
  readonly prevInLaneRequested = output<void>();

  /** Lane-pager snapshot state for the header (read-only facades). */
  private readonly lanePager = inject(LanePagerService);
  readonly pagerPosition = this.lanePager.position;
  readonly pagerTotal = this.lanePager.total;
  readonly pagerCanPrev = this.lanePager.canPrev;
  readonly pagerCanNext = this.lanePager.canNext;
  readonly pagerLaneLabel = this.lanePager.laneLabel;

  readonly editingPrompt = signal(false);

  // Three-pane layout — state, persistence and resize handlers live in
  // LayoutPanesService (provided locally on this component). The fields
  // below are facades so existing template bindings keep working.
  private readonly layout = inject(LayoutPanesService);
  readonly panesVisible = this.layout.panesVisible;
  readonly paneWeights = this.layout.paneWeights;
  readonly maximizedPane = this.layout.maximizedPane;

  // Live Claude session telemetry — owned by ClaudeSessionPollService
  // (5 s poll, started/stopped in response to detail() changes).
  private readonly claudePoll = inject(ClaudeSessionPollService);
  readonly claudeSession = this.claudePoll.session;
  readonly claudeRateLimit = this.claudePoll.rateLimit;

  // Per-job session-event log — drives the "session continued / lost"
  // chip in the protocol pane header. Polled at a slower 10 s cadence
  // because events only flip on start/continue/recovery, not per turn.
  private readonly sessionEventsPoll = inject(SessionEventsPollService);

  // Per-job run timeline (CLI invocations between user inputs). Drives
  // the run-list view in the protocol pane and the per-run commits
  // drill-down. Polled at 5 s; the activity log poll is the source of
  // sub-second tail updates.
  private readonly runTimelinePoll = inject(RunTimelinePollService);

  // Git view state lives in GitPaneService (provided locally on this
  // component). Facades below keep the existing call sites unchanged.
  private readonly git = inject(GitPaneService);
  readonly gitStatus = this.git.status;
  readonly gitLoading = this.git.loading;
  readonly selectedDiffPath = this.git.selectedDiffPath;
  readonly gitDiffText = this.git.diffText;
  readonly commitMessage = this.git.commitMessage;
  readonly committing = this.git.committing;
  readonly generatingMsg = this.git.generatingMsg;
  // CLI output buffer + run-state lives in CliOutputPollService.
  private readonly cliPoll = inject(CliOutputPollService);
  readonly cliOutput = this.cliPoll.output;
  readonly isRunning = this.cliPoll.isRunning;
  readonly startedAt = this.cliPoll.startedAt;
  readonly elapsedTime = this.cliPoll.elapsedTime;
  readonly errorMsg = signal<string | null>(null);
  readonly starting = signal(false);
  readonly continuing = signal(false);
  readonly regeneratingSummary = signal(false);
  private regenPollTimer: ReturnType<typeof setInterval> | null = null;
  private regenStartedAt = 0;
  readonly followupPrompt = signal('');
  readonly continueMode = signal<ContinueMode>('continue');
  readonly modelDraft = signal('');
  readonly availableModels = signal<CliModelInfo[]>([]);
  readonly cliTypes = CLI_TYPES;
  readonly cliTypeDraft = signal<CliType>('copilot');
  readonly modelCatalogSource = signal<string>('');

  modelMultiplier(id: string | null | undefined): number | null {
    if (!id) return null;
    return this.availableModels().find(m => m.id === id)?.multiplier ?? null;
  }

  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }
  readonly showCliConfig = signal(false);
  readonly cliStatus = signal<CliSettings | null>(null);
  readonly cliPathDraft = signal('');
  readonly cliTestResult = signal<CliSettings | null>(null);
  readonly cliTesting = signal(false);
  readonly showLogOverlay = signal(false);
  readonly activeInspectorTab = signal<'protocol' | 'activity'>('protocol');
  /** When true while a run is active, the user has manually expanded the
   *  setup bar and we keep it expanded until the run ends or they toggle it
   *  off. Reset on job switch and when the run ends so the next run starts
   *  collapsed again. */
  readonly setupExpandedDuringRun = signal(false);
  /** Effective collapsed state: auto-collapse while running, unless the user
   *  explicitly hit "Show setup". Always expanded when not running. */
  readonly setupCollapsed = computed(() => this.isRunning() && !this.setupExpandedDuringRun());
  readonly tokenDraft = signal('');
  readonly showToken = signal(false);
  readonly tokenSaving = signal(false);
  readonly editingTitle = signal(false);
  readonly titleDraft = signal('');
  readonly savingTitle = signal(false);
  readonly completingAndNext = signal(false);
  readonly changingState = signal(false);
  readonly movingToTop = signal(false);
  /** Stable id of the triage button currently in flight (null when idle). */
  readonly triageActingId = signal<string | null>(null);
  readonly detailPanePercent = this.layout.detailPanePercent;

  /** 0-based index of the open job in `lanePeers`. -1 when the parent has not
   *  resolved peers yet (e.g. during the optimistic move grace window). */
  readonly laneIndex = computed(() => {
    const peers = this.lanePeers();
    const key = this.detail().info.jobKey;
    return peers.findIndex(p => p.jobKey === key);
  });
  readonly laneSize = computed(() => this.lanePeers().length);

  @ViewChild('triagePanel') private triagePanelRef?: TriagePanelComponent;

  // Wall-clock tick used by relative-time formatters (e.g. formatResetIn).
  // Sourced from NowTickService — keeps the formatter stable within one
  // change-detection cycle and avoids the NG0100 minute-boundary trap.
  private readonly nowTick = inject(NowTickService).now;

  promptDraftValue = '';
  // Tracks whether the user has explicitly chosen an inspector tab for the
  // current job. Reset on job switch. Used to block the "auto-switch to
  // Protocol once the summary lands" effect from clobbering a manual choice.
  private userTouchedInspectorTab = false;
  private lastCliConfigRequest = 0;
  private currentJobKey: string | null = null;
  // Tracks which failed execution we've already surfaced as a modal so that
  // re-opening the detail view (or the 2s board refresh re-emitting the same
  // failed snapshot) does not re-pop the dialog. Keyed by `${jobKey}|${startedAt}`.
  private lastShownFailureKey: string | null = null;

  constructor(private jobService: JobService, private errorDialog: ErrorDialogService) {
    // Load the initial catalog for whatever CLI the current job uses; the effect below
    // will re-trigger this when the user switches CLIs.
    this.loadModelCatalog('copilot');
    // Register the detail view as the bottom of the modal stack while it is
    // mounted. Any modal opened on top of it (Add Task, error dialog, verbose
    // debug, confirm-dialog) registers later and therefore wins Escape first.
    // When no modal is on top, Escape closes the detail view itself — except
    // while an inline title/prompt edit or one of the local sub-overlays
    // (log overlay, CLI config) is active; those have their own Escape
    // affordances (template `(keydown.escape)` on the input) and would feel
    // broken if a single Escape jumped past them and closed the panel.
    inject(ModalStackService).pushUntilDestroyed(
      'job-detail',
      () => {
        // Decline (return false) while an inline title/prompt edit or a
        // local sub-overlay is active. The original template binding
        // `(keydown.escape)` on the title input then runs and cancels
        // the edit; closing the whole panel would feel broken.
        if (this.editingTitle() || this.editingPrompt()) return false;
        if (this.showLogOverlay()) {
          this.showLogOverlay.set(false);
          return true;
        }
        if (this.showCliConfig()) {
          this.showCliConfig.set(false);
          return true;
        }
        this.back.emit();
        return true;
      },
      inject(DestroyRef),
    );
  }

  private loadModelCatalog(cliType: CliType) {
    this.jobService.getCliModelCatalog(cliType).subscribe({
      next: (catalog) => {
        const models = catalog.models ?? [];
        this.availableModels.set(models);
        this.modelCatalogSource.set(catalog.source ?? '');
        if (!this.modelDraft()) {
          const def = models.find(m => m.isDefault);
          if (def) this.modelDraft.set(def.id);
        }
      },
      error: () => {
        this.availableModels.set([]);
      }
    });
  }

  onCliTypeChange(value: string) {
    if (!CLI_TYPES.includes(value as CliType)) return;
    const next = value as CliType;
    if (next === this.cliTypeDraft()) return;
    this.cliTypeDraft.set(next);
    // Switching CLI clears the previous model — let the user pick one for the new backend.
    this.modelDraft.set('');
    this.loadModelCatalog(next);

    this.jobService.setJobCliType(this.detail().info.id, next, this.detail().info.watchPath).subscribe({
      next: () => this.fileSaved.emit(),
      error: (err) => this.showError(err)
    });
  }

  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }

  /** When the run ends, drop the user's "Show setup" override so the next run
   *  starts compact again. Idempotent — only writes when the flag would change. */
  private resetSetupExpandWhenIdle = effect(() => {
    if (!this.isRunning() && this.setupExpandedDuringRun()) {
      this.setupExpandedDuringRun.set(false);
    }
  });

  /** Clear the lane-dropdown pending flag once the parent has re-fetched the
   *  detail and the new `state` arrives. Without this the select stays
   *  disabled forever after a successful move. The watch is on the raw
   *  state string so it also fires for moves the parent triggered via
   *  drag-and-drop on the kanban behind the open detail view. */
  private lastObservedState: string | null = null;
  private resetChangingStateOnUpdate = effect(() => {
    const state = this.detail().info.state;
    if (this.lastObservedState !== null && state !== this.lastObservedState && this.changingState()) {
      this.changingState.set(false);
    }
    this.lastObservedState = state;
  });

  private detailEffect = effect(() => {
    const d = this.detail();
    const isJobSwitch = this.currentJobKey !== d.info.jobKey;
    this.currentJobKey = d.info.jobKey;
    // Keep GitPaneService in sync with the open job; resets internal
    // state on actual job changes, no-ops on same-job refreshes.
    this.git.setJob(d.info);

    this.errorMsg.set(null);
    if (d.info.model) {
      this.modelDraft.set(d.info.model);
    } else {
      const def = this.availableModels().find(m => m.isDefault);
      this.modelDraft.set(def?.id ?? '');
    }
    const nextCliType = (d.info.cliType ?? 'copilot') as CliType;
    if (nextCliType !== this.cliTypeDraft()) {
      this.cliTypeDraft.set(nextCliType);
      this.loadModelCatalog(nextCliType);
    }

    if (isJobSwitch) {
      // Reset job-scoped UI state only when switching to a different job —
      // refreshes for the same job (e.g. execution status changes) must
      // preserve the live CLI output and view state.
      this.showLogOverlay.set(false);
      // Default tab:
      //  • In-progress jobs always start on Activity — the live CLI output is
      //    what the user wants to see; any existing protocol from a prior run
      //    is stale until the current run finishes.
      //  • Otherwise: Protocol if a summary exists, else Activity.
      // The auto-switch effect below promotes Activity → Protocol once
      // Haiku finishes, unless the user has manually picked a tab.
      const isInProgress = d.info.state === '3-progress';
      this.activeInspectorTab.set(isInProgress ? 'activity' : (d.statusMarkdown ? 'protocol' : 'activity'));
      this.userTouchedInspectorTab = false;
      this.showCliConfig.set(false);
      this.cliTestResult.set(null);
      this.editingPrompt.set(false);
      this.editingTitle.set(false);
      this.savingTitle.set(false);
      this.followupPrompt.set('');
      this.setupExpandedDuringRun.set(false);
      this.cliPoll.resetForJobSwitch();
      this.lastShownFailureKey = null;
    }

    // Auto-promote Activity → Protocol the moment a fresh summary lands,
    // but only when the user hasn't actively chosen a tab themselves and the
    // job has left 3-progress — while the run is live we keep showing the
    // activity log even if a stale summary from a previous attempt exists.
    if (
      !this.userTouchedInspectorTab &&
      this.activeInspectorTab() === 'activity' &&
      d.info.state !== '3-progress' &&
      d.summaryState?.status === 'ready' &&
      d.statusMarkdown
    ) {
      this.activeInspectorTab.set('protocol');
    }

    // Symmetric counterpart: when a job we're watching transitions into
    // 3-progress (runner auto-pickup, manual start, or continuation from
    // review), demote Protocol → Activity so the live CLI output is what
    // the user sees instead of a stale summary from the previous run. Only
    // applies when the user hasn't manually picked the protocol tab.
    if (
      !this.userTouchedInspectorTab &&
      this.activeInspectorTab() === 'protocol' &&
      d.info.state === '3-progress'
    ) {
      this.activeInspectorTab.set('activity');
    }

    // Manual-regenerate poll lifecycle: once the backend flips out of
    // "generating", stop hammering the detail endpoint.
    if (this.regeneratingSummary() && d.summaryState?.status !== 'generating') {
      // Honour the small grace window (the very first request response usually
      // lands before the in-process state has flipped to "generating").
      if (Date.now() - this.regenStartedAt > 1500) {
        this.stopRegenPolling();
      }
    }

    this.cliPoll.setJob({ id: d.info.id, watchPath: d.info.watchPath });
    this.applyExecutionState(d.info.execution);

    // The endpoint returns the live buffer while a process is active and falls
    // back to logs/cli-output.log for completed tasks.
    if (d.info.execution?.status === 'running' && !this.cliPoll.isPolling()) {
      this.cliPoll.startPolling();
    }
    this.jobService.getJobOutput(d.info.id, d.info.watchPath).subscribe({
      next: (output) => this.cliPoll.hydrateOutput(output, d.info.execution?.startedAt ?? null),
      error: (err) => {
        if (err.status !== 0) return; // silent for 404 etc
        this.showError(err);
      }
    });

  });
  private cliConfigEffect = effect(() => {
    const requestId = this.errorDialog.cliConfigRequest();
    if (requestId === 0 || requestId === this.lastCliConfigRequest) {
      return;
    }

    this.lastCliConfigRequest = requestId;
    this.openCliConfig();
  });
  private gitAutoRefreshEffect = effect(() => {
    // Only the active task's detail view shows the working tree; polling
    // git status on a non-active task would just churn for nothing and
    // would also pull working-tree state for a task that, by the
    // worktree-isolation rule, doesn't get to render that data.
    if (this.panesVisible().git && this.isActiveJob()) {
      this.git.startAutoRefresh();
    } else {
      this.git.stopAutoRefresh();
    }
  });

  ngOnDestroy() {
    this.detailEffect.destroy();
    this.cliConfigEffect.destroy();
    this.gitAutoRefreshEffect.destroy();
    this.cliPoll.stop();
    this.layout.stopLayoutResize();
    this.claudePoll.stop();
    this.stopRegenPolling();
  }

  /**
   * Re-run the Haiku summary for the current job. The backend writes status.md
   * and flips summaryState through generating → ready/failed; we poll detail
   * every 2 s (via fileSaved → parent re-fetch) so the UI follows the
   * transition. The detailEffect stops the timer once the status leaves
   * "generating".
   */
  regenerateProtocol(): void {
    if (this.regeneratingSummary()) return;
    const { id, watchPath } = this.detail().info;
    this.regeneratingSummary.set(true);
    this.regenStartedAt = Date.now();
    this.jobService.regenerateSummary(id, watchPath).subscribe({
      next: () => {
        // Immediate refresh — status flips to "generating" so the spinner shows.
        this.fileSaved.emit();
        this.startRegenPolling();
      },
      error: (err) => {
        this.stopRegenPolling();
        this.showError(err);
      }
    });
  }

  private startRegenPolling(): void {
    this.stopRegenPolling(false);
    this.regenPollTimer = setInterval(() => {
      // Hard cap: Haiku itself times out at 90 s; give a bit of slack for
      // process spawn + status file flush.
      if (Date.now() - this.regenStartedAt > 120_000) {
        this.stopRegenPolling();
        return;
      }
      this.fileSaved.emit();
    }, 2000);
  }

  private stopRegenPolling(clearFlag = true): void {
    if (this.regenPollTimer != null) {
      clearInterval(this.regenPollTimer);
      this.regenPollTimer = null;
    }
    if (clearFlag) this.regeneratingSummary.set(false);
  }

  // Bridge detail() changes to the ClaudeSessionPollService. The service
  // ignores no-op syncs and re-arms its 5 s timer only when the polled
  // job actually changes.
  private readonly claudeSessionEffect = effect(() => {
    this.claudePoll.syncTo(this.detail()?.info ?? null);
  });

  // Same bridge for the session-event poller (10 s cadence).
  private readonly sessionEventsEffect = effect(() => {
    this.sessionEventsPoll.syncTo(this.detail()?.info ?? null);
  });

  // ...and for the run-timeline poller (5 s cadence).
  private readonly runTimelineEffect = effect(() => {
    this.runTimelinePoll.syncTo(this.detail()?.info ?? null);
  });

  // ...and for the per-job screenshots poller (10 s cadence). The
  // protocol pane reads its signal directly via inject().
  private readonly screenshotsPoll = inject(ScreenshotsPollService);
  private readonly screenshotsEffect = effect(() => {
    this.screenshotsPoll.syncTo(this.detail()?.info ?? null);
  });

  canStartJob(): boolean {
    const state = this.detail().info.state;
    return (state === '2-ready' || state === '3-progress') && !this.isRunning();
  }

  /**
   * When the Start button is shown but should not actually fire, return a
   * short reason for the tooltip and disabled state. Today's case: another
   * job in the same project is already running on a CLI - the backend
   * rejects starts in this state, so disabling the button before the click
   * keeps the user from chasing a 4xx round-trip and explains why.
   */
  startDisabledReason = computed<string | null>(() => {
    const info = this.detail()?.info;
    if (!info) return null;
    const status = this.jobService.runnerStatus();
    const project = status?.projects?.[info.projectName];
    if (!project) return null;
    const activeId = project.activeJobId;
    if (!activeId || activeId === info.id) return null;
    return `Project "${info.projectName}" is already running ${activeId}. Stop the active run first or wait for it to finish.`;
  });

  /**
   * Whether the displayed task is the runner's currently-active job for
   * its project. Drives the worktree-isolation rule: working-tree info
   * (live `git status`, the "Accepted task work uncommitted" hygiene
   * warning) is only shown on the active task. Non-active tasks see only
   * their committed evidence; their detail view never speaks for changes
   * that belong to whichever task the agent is currently editing.
   */
  readonly isActiveJob = computed<boolean>(() => {
    const info = this.detail()?.info;
    if (!info) return false;
    const status = this.jobService.runnerStatus();
    const project = status?.projects?.[info.projectName];
    return project?.activeJobId === info.id;
  });

  startJob(): void {
    this.errorMsg.set(null);
    this.starting.set(true);
    const model = this.modelDraft().trim() || undefined;
    this.jobService.startJob(this.detail().info.id, this.detail().info.watchPath, model).subscribe({
      next: (resp) => {
        this.starting.set(false);
        if (resp.status === 'started' && resp.execution) {
          this.cliPoll.beginRun(new Date(resp.execution.startedAt));
          this.sessionEventsPoll.refresh();
        }
        // status === 'queued': no modal, no error. The orchestrator's
        // [queued] meta line lands in the activity log on the next poll
        // tick. The job's pendingIntent badge will appear on the card.
      },
      error: (err) => {
        this.starting.set(false);
        this.showError(err);
      }
    });
  }

  stopJob(): void {
    this.errorMsg.set(null);
    this.jobService.stopJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: () => this.cliPoll.stop(),
      error: (err) => this.showError(err)
    });
  }

  continueJob(): void {
    const prompt = this.followupPrompt().trim();
    if (!prompt) return;

    this.errorMsg.set(null);
    this.continuing.set(true);
    // Echo the user's message into the activity log immediately so the chat
    // feels responsive — the backend writes the same line to cli-output.log
    // on success, and the next poll dedupes the optimistic copy.
    this.cliPoll.appendOptimisticUserMessage(prompt);
    this.followupPrompt.set('');
    const model = this.modelDraft().trim() || undefined;
    this.jobService.continueJob(this.detail().info.id, prompt, this.detail().info.watchPath, model, undefined, this.continueMode()).subscribe({
      next: (resp) => {
        this.continuing.set(false);
        if (resp.status === 'started' && resp.execution) {
          this.cliPoll.beginContinuation(new Date(resp.execution.startedAt));
          this.sessionEventsPoll.refresh();
        }
        // status === 'queued': the project was busy. The backend already
        // saved the user's intent + posted a [queued] orchestrator line
        // into the chat; nothing more for us to do here. The optimistic
        // user-message echo above stays so the chat reads forward.
      },
      error: (err) => {
        this.continuing.set(false);
        // Restore the user's text so they can correct or retry — the backend
        // never accepted it, so the optimistic echo above is a lie we shouldn't
        // leave on screen permanently. We keep it visible for now (so the user
        // can see what they tried) but the inline error banner explains why.
        this.followupPrompt.set(prompt);
        this.showError(err);
      }
    });
  }

  canSendChat(): boolean {
    if (this.continuing()) return false;
    if (!this.followupPrompt().trim()) return false;
    return true;
  }

  chatSendLabel(): string {
    if (this.continuing()) return '⏳ Sending...';
    return this.isRunning() ? '⏸ Pause & Send' : '▶ Send';
  }

  sendChatMessage(): void {
    const prompt = this.followupPrompt().trim();
    if (!prompt || this.continuing()) return;

    if (!this.isRunning()) {
      this.continueJob();
      return;
    }

    // Pause-and-send: stop the running CLI first, then continue with the
    // user's intervention as a follow-up prompt. Reason 'followup' tells
    // the backend to mark the resulting CliExecution as 'stopped' (not
    // 'failed', exitCode -1) so applyExecutionState below does not pop a
    // crash modal between the kill and the follow-up start.
    this.errorMsg.set(null);
    this.continuing.set(true);
    this.jobService.stopJob(this.detail().info.id, this.detail().info.watchPath, 'followup').subscribe({
      next: () => {
        this.isRunning.set(false);
        this.continuing.set(false);
        this.continueJob();
      },
      error: (err) => {
        this.continuing.set(false);
        this.showError(err);
      }
    });
  }

  onModelDraftChange(value: string): void {
    const trimmed = (value ?? '').trim();
    this.modelDraft.set(trimmed);
    const current = this.detail().info.model ?? '';
    if (trimmed === current) return;

    this.jobService.setJobModel(
      this.detail().info.id,
      trimmed === '' ? null : trimmed,
      this.detail().info.watchPath
    ).subscribe({
      error: (err) => this.showError(err)
    });
  }

  private showError(err: any): void {
    const message = err.status === 0
      ? 'Backend not reachable — is the API running on localhost:5030?'
      : err.error?.error || (typeof err.error === 'string' ? err.error : `Request failed (${err.status || 'unknown'}): ${err.statusText || err.message || 'Unknown error'}`);

    this.errorMsg.set(message);
    this.errorDialog.show(err, {
      title: 'Task action failed',
      fallbackMessage: message,
      source: `Task ${this.detail().info.id}`,
      canOpenCliConfig: this.canOpenCliConfigForCurrentJob(message)
    });
  }

  private applyExecutionState(execution: import('../../models/job.model').CliExecution | null): void {
    if (!execution) return;
    this.cliPoll.applyExecution(execution);
    // 'stopped' is the deliberate-kill status (user pause, Pause-&-Send,
    // watchdog kill, host shutdown). It is not a crash and must NOT open
    // the failure modal; otherwise every Pause-&-Send produces a false
    // alarm. Clear any stale error banner left over from a real prior
    // failure on the same job so the UI does not look broken.
    if (execution.status === 'stopped') {
      this.errorMsg.set(null);
      return;
    }
    if (shouldShowFailureToast(execution)) {
      const message = execution.exitCode === null
        ? 'Task execution failed.'
        : `Task execution failed with exit code ${execution.exitCode}.`;
      this.errorMsg.set(message);

      // The backend keeps the failed CliExecution in memory until the next run,
      // so the same snapshot arrives on every detail refresh and on every job
      // re-open. Without de-duping we'd block the detail view behind a modal
      // every 2 s. Key by jobKey + startedAt so a fresh failure (different
      // startedAt) still surfaces.
      const failureKey = `${execution.jobKey}|${execution.startedAt}`;
      if (this.lastShownFailureKey === failureKey) return;
      this.lastShownFailureKey = failureKey;

      this.errorDialog.show(message, {
        title: 'Task execution failed',
        fallbackMessage: message,
        source: `Task ${this.detail().info.id}`,
        output: { execution, cliOutput: this.cliOutput() }
      });
    }
  }

  isProgress(): boolean {
    return this.detail().info.state === '3-progress';
  }

  isReview(): boolean {
    const s = this.detail().info.state;
    // ADR-0025: any review lane (auto or human) counts as "in review" for
    // the detail-pane affordances. Legacy 4-review supported during
    // transition.
    return s === '4-auto-review' || s === '5-human-review' || s === '4-review';
  }

  /**
   * Triage panel: route a typed lane action to the right handler. Move /
   * delete / move-to-top go to the parent (it owns the optimistic-paint and
   * auto-advance to the next peer); start / stop / editPrompt / showActivity
   * are local operations against the open job.
   */
  onTriageAction(payload: TriageActionPayload): void {
    if (this.mutationsBlocked() || this.triageActingId() !== null) return;
    const { id, intent } = payload;
    switch (intent.kind) {
      case 'move':
        this.triageActingId.set(id);
        this.triageMoveRequested.emit({ targetState: intent.targetState, actionId: id });
        return;
      case 'moveToTop':
        this.triageActingId.set(id);
        this.triageMoveToTopRequested.emit({ actionId: id });
        return;
      case 'delete':
        this.triageActingId.set(id);
        this.triageDeleteRequested.emit({ actionId: id });
        return;
      case 'start':
        this.triageActingId.set(id);
        this.triageStartRequested.emit({ actionId: id });
        return;
      case 'stop':
        this.triageActingId.set(id);
        this.stopJob();
        // Stop is local-only; clear after the request kicks off.
        queueMicrotask(() => this.triageActingId.set(null));
        return;
      case 'editPrompt':
        if (!this.panesVisible().prompt) this.togglePane('prompt');
        this.startEdit('prompt');
        return;
      case 'showActivity':
        this.activeInspectorTab.set('activity');
        this.userTouchedInspectorTab = true;
        return;
    }
  }

  /** Called by the parent once a triage move/delete settles, to reset the
   *  per-button spinner. The parent calls `clearTriageActing()` whether or
   *  not the open job changed (auto-advance replaces the panel; same-lane
   *  actions like Run Now leave it where it is). */
  clearTriageActing(): void {
    this.triageActingId.set(null);
  }

  /**
   * Keyboard navigation for triage mode: `j` / ↓ for next, `k` / ↑ for prev,
   * `Enter` for the lane's primary action. Suppressed while the user is typing
   * in an input/textarea/contenteditable so chat compose and prompt edit keep
   * working.
   *
   * Escape is handled separately via `ModalStackService`: the detail view
   * registers itself on the stack when it mounts and any modal opened on top
   * (Add Task, error dialog, verbose-debug overlay, confirm-dialog, ...)
   * sits above it, so Escape closes the modal first and leaves the detail
   * open. The previous local `case 'Escape'` here is gone.
   */
  @HostListener('document:keydown', ['$event'])
  onTriageKey(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
    if (event.metaKey || event.ctrlKey || event.altKey) return;
    const target = event.target as HTMLElement | null;
    if (target) {
      const tag = target.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      if (target.isContentEditable) return;
    }
    if (this.editingTitle() || this.editingPrompt()) return;
    if (this.showLogOverlay() || this.showCliConfig()) return;

    switch (event.key) {
      case 'j':
      case 'ArrowDown':
      case 'ArrowRight':
        event.preventDefault();
        this.nextInLaneRequested.emit();
        return;
      case 'k':
      case 'ArrowUp':
      case 'ArrowLeft':
        event.preventDefault();
        this.prevInLaneRequested.emit();
        return;
      case 'Enter':
        if (this.mutationsBlocked() || this.triageActingId() !== null) return;
        this.triagePanelRef?.triggerPrimary();
        event.preventDefault();
        return;
    }
  }

  completeAndNext() {
    if (this.completingAndNext()) return;
    this.completingAndNext.set(true);
    const { id, watchPath } = this.detail().info;
    this.jobService.moveJob(id, '6-completed', watchPath).subscribe({
      next: () => {
        this.completingAndNext.set(false);
        this.completeAndNextReview.emit();
      },
      error: (err) => {
        this.completingAndNext.set(false);
        this.errorDialog.show(err, {
          title: 'Failed to complete task',
          fallbackMessage: 'Failed to move task to Completed',
          source: `Task ${id}`
        });
      }
    });
  }

  /**
   * Forwarded from the detail-header lane dropdown. The parent owns the
   * actual move (so the board's optimistic-paint / detail re-fetch stay in
   * the same place as drag-and-drop moves on the kanban). We only flip the
   * local "changing" flag for the disabled-while-pending UX; the parent
   * resolves it by re-feeding `[detail]` after the move settles.
   */
  onStateChange(targetState: string) {
    if (this.changingState()) return;
    if (targetState === this.detail().info.state) return;
    this.changingState.set(true);
    this.stateChangeRequested.emit({ targetState });
  }

  /**
   * "Do Next": jump this 2-ready task to the head of the Ready queue so the
   * project's runner picks it up on the next tick. The backend reorder is
   * atomic (POST /api/jobs/{id}/move-to-top → JobStateMachine.PromoteToReadyTop),
   * which preserves any earlier-queued PendingIntent jobs and avoids the
   * stale-grouped() race the optimistic-reorder path had.
   */
  moveToTopOfReady(): void {
    if (this.movingToTop()) return;
    const info = this.detail().info;
    if (info.state !== '2-ready') return;

    this.movingToTop.set(true);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJobToTop(info.id, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.movingToTop.set(false);
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        this.movingToTop.set(false);
        this.errorDialog.show(err, {
          title: 'Failed to move task to top',
          fallbackMessage: 'Failed to move task to the top of the Ready queue',
          source: `Task ${info.id}`
        });
      }
    });
  }

  startEdit(which: 'prompt') {
    if (this.isRunning()) return;
    if (which === 'prompt') {
      this.promptDraftValue = this.detail().promptMarkdown ?? '';
      this.editingPrompt.set(true);
    }
  }

  /**
   * Forwarded from the protocol pane's pill toggle. Marks the user as having
   * manually picked a tab so the auto-switch from "activity → protocol on
   * summary ready" doesn't override their explicit choice.
   */
  onInspectorTabChange(tab: 'protocol' | 'activity') {
    this.userTouchedInspectorTab = true;
    this.activeInspectorTab.set(tab);
  }

  /** Flips the setup bar between compact (default while running) and the full
   *  selectors. Only meaningful while a run is active — when not running, the
   *  bar is always expanded and the toggle isn't shown. */
  toggleSetupCollapsed() {
    this.setupExpandedDuringRun.update(v => !v);
  }

  startTitleEdit() {
    if (this.editingTitle()) return;
    this.titleDraft.set(this.detail().info.title || this.detail().info.id);
    this.editingTitle.set(true);
  }

  cancelTitleEdit() {
    this.editingTitle.set(false);
    this.savingTitle.set(false);
  }

  saveTitle() {
    const trimmed = this.titleDraft().trim();
    if (!trimmed || this.savingTitle()) return;
    const current = this.detail().info.title || this.detail().info.id;
    if (trimmed === current) {
      this.editingTitle.set(false);
      return;
    }

    this.savingTitle.set(true);
    this.jobService.setJobTitle(this.detail().info.id, trimmed, this.detail().info.watchPath).subscribe({
      next: () => {
        this.savingTitle.set(false);
        this.editingTitle.set(false);
        this.fileSaved.emit();
      },
      error: (err) => {
        this.savingTitle.set(false);
        this.showError(err);
      }
    });
  }

  cancelEdit(which: 'prompt') {
    if (which === 'prompt') this.editingPrompt.set(false);
  }

  saveFile(fileName: string) {
    if (this.isRunning()) return;
    if (fileName !== 'prompt.md') return;
    this.saveFileContent(fileName, this.promptDraftValue);
  }

  saveFileContent(fileName: string, content: string) {
    if (this.isRunning()) return;
    this.jobService.updateJobFile(this.detail().info.id, fileName, content, this.detail().info.watchPath).subscribe({
      next: () => {
        if (fileName === 'prompt.md') this.editingPrompt.set(false);
        this.fileSaved.emit();
      },
      error: (err) => this.showError(err)
    });
  }

  handleFileKeydown(event: KeyboardEvent, fileName: string): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.saveFile(fileName);
    }
  }

  renderMarkdown(markdown: string): string {
    return markdownToHtml(markdown);
  }

  // === 3-pane layout — facades for LayoutPanesService ====================

  startLayoutResize(event: PointerEvent): void { this.layout.startLayoutResize(event); }

  togglePane(name: 'prompt' | 'protocol' | 'git'): void {
    const next = this.layout.togglePane(name);
    if (name === 'git' && next.git && !this.gitStatus()) {
      // Lazy-load git status the first time the pane is shown.
      this.refreshGit();
    }
  }

  toggleMaximize(name: 'prompt' | 'protocol' | 'git'): void { this.layout.toggleMaximize(name); }

  isPaneRendered(name: 'prompt' | 'protocol' | 'git'): boolean { return this.layout.isPaneRendered(name); }

  firstVisibleAfter(name: 'prompt' | 'protocol' | 'git'): 'protocol' | 'git' { return this.layout.firstVisibleAfter(name); }

  startPaneResize(event: PointerEvent, left: 'prompt' | 'protocol', right: 'protocol' | 'git'): void {
    this.layout.startPaneResize(event, left, right);
  }

  // === Git view facades ==================================================
  // State + API calls live in GitPaneService (provided locally). The
  // GitPaneComponent in the template binds directly to the service; the
  // wrappers here keep older same-class call sites working (e.g. the
  // togglePane lazy-load below).

  refreshGit(): void { this.git.refresh(); }
  openInVsCode(): void { this.git.openInVsCode(); }

  // === Claude live session telemetry =====================================
  // Polling lives in ClaudeSessionPollService (provided locally on this
  // component); the claudeSessionEffect above bridges detail() changes
  // into it, and the session/rateLimit signals are exposed as facades.

  formatTokens(n: number): string { return fmtTokens(n); }

  claudeSessionTooltip(): string {
    const cs = this.claudeSession();
    if (!cs) return '';
    return [
      `Model: ${cs.model ?? '?'}`,
      `Input: ${cs.inputTokens.toLocaleString()} tokens`,
      `Output: ${cs.outputTokens.toLocaleString()} tokens`,
      `Cache read: ${cs.cacheReadTokens.toLocaleString()} tokens`,
      `Cache creation: ${cs.cacheCreationTokens.toLocaleString()} tokens`,
      `Turns recorded: ${cs.turnCount}`,
      cs.lastTurnAt ? `Last turn: ${cs.lastTurnAt}` : ''
    ].filter(Boolean).join('\n');
  }

  formatRateWindow(window: string | null): string { return fmtRateWindow(window); }

  formatResetIn(epochSeconds: number): string { return fmtResetIn(epochSeconds, this.nowTick()); }

  rateLimitTooltip(): string {
    const rl = this.claudeRateLimit();
    if (!rl) return '';
    const reset = rl.resetsAt
      ? new Date(rl.resetsAt * 1000).toLocaleString()
      : 'unknown';
    return [
      `Window: ${this.formatRateWindow(rl.window)}`,
      `Status: ${rl.status ?? '?'}`,
      `Resets at: ${reset}`,
      `Overage: ${rl.overageStatus ?? '—'}`,
      rl.isUsingOverage ? 'Currently using overage budget' : '',
      `Captured: ${new Date(rl.capturedAt).toLocaleTimeString()}`
    ].filter(Boolean).join('\n');
  }

  stateLabel(state: string): string { return fmtStateLabel(state); }

  formatTime(dateStr: string): string { return fmtTime(dateStr); }

  formatDate(dateStr: string): string { return fmtDate(dateStr); }

  formatDateTime(dateStr: string): string { return fmtDateTime(dateStr); }

  isCliError(): boolean {
    const msg = this.errorMsg();
    return this.isCliErrorMessage(msg);
  }

  openCliConfig(): void {
    if (this.cliTypeDraft() !== 'copilot') return;
    this.showCliConfig.set(true);
    this.cliTestResult.set(null);
    this.jobService.getCliSettings().subscribe({
      next: (settings) => {
        this.cliStatus.set(settings);
        this.cliPathDraft.set(settings.path);
      },
      error: (err) => this.showError(err)
    });
  }

  dismissError(): void {
    this.errorMsg.set(null);
    this.showCliConfig.set(false);
  }

  testCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.cliTestResult.set(null);
    this.jobService.testCliPath(path).subscribe({
      next: (result) => {
        this.cliTestResult.set(result);
        this.cliTesting.set(false);
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.jobService.setCliPath(path).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.cliTestResult.set(null);
        this.cliTesting.set(false);
        if (result.available) {
          this.errorMsg.set(null);
          this.showCliConfig.set(false);
        }
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveToken(): void {
    const token = this.tokenDraft().trim();
    if (!token) return;
    this.tokenSaving.set(true);
    this.jobService.setGitHubToken(token).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.tokenSaving.set(false);
        this.tokenDraft.set('');
        if (result.hasToken && result.available) {
          this.errorMsg.set(null);
        }
      },
      error: (err) => {
        this.tokenSaving.set(false);
        this.showError(err);
      }
    });
  }

  onProjectChange(targetWatchPath: string) {
    if (targetWatchPath === this.detail().info.watchPath) return;
    this.jobService.changeProject(this.detail().info.id, targetWatchPath, this.detail().info.watchPath).subscribe({
      next: () => this.projectChanged.emit(targetWatchPath),
      error: (err) => this.showError(err)
    });
  }

  private isCliErrorMessage(message: string | null | undefined): boolean {
    return !!message && /cli|copilot|authenticat/i.test(message);
  }

  private canOpenCliConfigForCurrentJob(message: string | null | undefined): boolean {
    return this.cliTypeDraft() === 'copilot' && this.isCliErrorMessage(message);
  }
}
