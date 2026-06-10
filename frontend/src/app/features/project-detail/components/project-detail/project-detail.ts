import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { GroupedJobs, ProjectQueueHealth, RunnerStatus } from '../../../../models/task.model';
import { CLI_TYPES, TaskState, type CliType } from '../../../../models/task.model';
import { cliTypeLabel, cliTypeIcon } from '../../../../services/format.util';
import type { OrchestratorLogEntry, OrchestratorSession } from '../../../../features/orchestrator';
import { CliCatalogStore } from '../../../../services/cli-catalog.store';
import { TokenSummaryBlockComponent } from '../../../../features/tokens';
import { GlobalOrchestratorCardComponent } from '../../../../features/orchestrator';
import { ProjectArchitectureSectionComponent } from '../project-architecture-section/project-architecture-section';
import { ProjectDriftSectionComponent } from '../project-drift-section/project-drift-section';
import { ProjectDriftOverviewSectionComponent } from '../project-drift-overview-section/project-drift-overview-section';
import { RegressionRadarComponent } from '../../../../features/regression-radar';
import { ProjectSupervisorSectionComponent } from '../project-supervisor-section/project-supervisor-section';
import { ProjectMetaCycleSectionComponent } from '../project-meta-cycle-section/project-meta-cycle-section';
import { ProjectAnalysisReportsSectionComponent } from '../project-analysis-reports-section/project-analysis-reports-section';
import { ProjectWorkspaceSectionComponent } from '../project-workspace-section/project-workspace-section';
import { AutonomySliderComponent } from '../autonomy-slider/autonomy-slider';
import { AnalysisReport } from '../../../../models/analysis-report.model';
import { TooltipDirective } from '../../../../components/tooltip';
import { ProjectWorkflowSectionComponent } from '../project-workflow-section/project-workflow-section';
interface ProjectSettingsRow {
  autoCommit: boolean;
  autoPushStrategy: AutoPushStrategy;
  runnerMode: string | null;
  orchestratorModel: string | null;
}

type AutoPushStrategy = 'never' | 'on-completed' | 'always-immediate';

export type ProjectDetailView =
  | 'overview'
  | 'jobs'
  | 'settings'
  // Nav-rebuild step 2 (T5b): sections that used to live inside the 'settings'
  // view now render under their own view so the project-shell rails (Workflow)
  // and the workspace Admin → CLI & Modelle surface can mount them unchanged.
  // Same controls, same backend writes — only the mount location moved.
  | 'workflow'
  | 'cli'
  | 'orchestrator'
  | 'activity'
  | 'architecture'
  | 'drift'
  | 'observability';

/**
 * Project detail panel: name + paths, runner mode toggle, orchestrator
 * model selector, auto-commit toggle, job-state counts, the most recent
 * orchestrator entries with token totals, and a button to open the full
 * feed. Mounted as an overlay panel from the project-tabs ⚙ button.
 *
 * Read-mostly: only the three setting controls write back. Everything
 * else is polled (5s interval) so a backend change made elsewhere is
 * reflected without manual refresh.
 */
@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [
    FormsModule,
    TokenSummaryBlockComponent,
    GlobalOrchestratorCardComponent,
    ProjectArchitectureSectionComponent,
    ProjectDriftSectionComponent,
    ProjectDriftOverviewSectionComponent,
    RegressionRadarComponent,
    ProjectSupervisorSectionComponent,
    ProjectMetaCycleSectionComponent,
    ProjectAnalysisReportsSectionComponent,
    ProjectWorkspaceSectionComponent,
    ProjectWorkflowSectionComponent,
    AutonomySliderComponent,
    TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-detail.html',
  styleUrl: './project-detail.scss'
})
export class ProjectDetailComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  readonly view = input<ProjectDetailView>('overview');
  readonly openFeed = output<string>();
  readonly openReport = output<AnalysisReport>();

  private readonly jobService = inject(TaskService);
  private readonly cliCatalog = inject(CliCatalogStore);

  readonly settings = signal<ProjectSettingsRow | null>(null);
  readonly runnerStatus = signal<RunnerStatus | null>(null);
  readonly grouped = signal<GroupedJobs | null>(null);
  readonly recentEntries = signal<OrchestratorLogEntry[]>([]);
  readonly orchSession = signal<OrchestratorSession | null>(null);
  readonly projectPaths = signal<{ path: string; rootPath: string | null; repositoryPath: string | null } | null>(null);
  readonly pendingDecisions = signal<readonly { jobId: string; title: string; reason: string | null }[]>([]);
  readonly queueHealth = signal<ProjectQueueHealth | null>(null);
  readonly queueRepairBusy = signal(false);
  readonly queueRepairMessage = signal<string | null>(null);

  readonly livePendingDecisions = signal<readonly { jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }[]>([]);
  readonly liveReplyDrafts: Record<string, string> = {};
  readonly liveReplySending: Record<string, boolean> = {};
  readonly liveReplyErrors: Record<string, string | null> = {};

  autoCommitDraft = false;
  autoPushStrategyDraft: AutoPushStrategy = 'on-completed';
  orchModelDraft = '';

  // Per-CLI permission/sandbox mode (YOLO default). One row per CLI shows the
  // effective mode + where it came from (project override / global config /
  // platform default), with a dropdown to override and a reset-to-default.
  // Defaults to YOLO so orchestrated pipeline runs never hang on a prompt.
  readonly cliTypes = CLI_TYPES;
  readonly cliModeResolved = signal<Record<string, { mode: string; source: string; args: string[] }>>({});
  readonly cliModeAvailable = signal<readonly string[]>(['yolo', 'workspace-write', 'read-only', 'custom']);
  /** Bound select value per CLI; mirrors the resolved effective mode until the operator picks one. */
  readonly cliModeDraft: Record<string, string> = {};
  /** Per-CLI write in flight; disables that row's controls until the PUT resolves. */
  readonly cliModeBusy: Record<string, boolean> = {};

  readonly cliModeMeta: Record<string, { label: string; hint: string }> = {
    'yolo': { label: 'YOLO', hint: 'Maximum autonomy — skips every permission/sandbox prompt. Default for agent-orchestrated runs.' },
    'workspace-write': { label: 'Workspace-Write', hint: 'Agent may write inside the workspace but is sandboxed from the wider system.' },
    'read-only': { label: 'Read-only', hint: 'Agent may read and plan but not write (plan-mode for Claude, read-only sandbox for Codex).' },
    'custom': { label: 'Custom', hint: 'Inject no permission flags — the CLI obeys whatever its own config files dictate.' },
  };
  readonly cliSourceMeta: Record<string, { label: string; hint: string }> = {
    'project': { label: 'project', hint: 'Set explicitly for this project — overrides global config and the platform default.' },
    'global': { label: 'global', hint: 'Inherited from the CLI’s own global config file (e.g. ~/.codex/config.toml).' },
    'default': { label: 'default', hint: 'Platform default (YOLO) — no project override and no global config detected.' },
  };

  cliModeLabel(mode: string | null | undefined) { return this.cliModeMeta[mode ?? '']?.label ?? mode ?? ''; }
  cliModeHint(mode: string | null | undefined) { return this.cliModeMeta[mode ?? '']?.hint ?? ''; }
  cliSourceLabel(source: string | null | undefined) { return this.cliSourceMeta[source ?? '']?.label ?? source ?? ''; }
  cliSourceHint(source: string | null | undefined) { return this.cliSourceMeta[source ?? '']?.hint ?? ''; }

  /** One row per CLI: effective mode + source + the args the next spawn will inject. */
  readonly cliModeRows = computed(() => {
    const resolved = this.cliModeResolved();
    return this.cliTypes.map((cli) => {
      const r = resolved[cli];
      return {
        cliType: cli as string,
        label: cliTypeLabel(cli as CliType),
        icon: cliTypeIcon(cli as CliType),
        mode: r?.mode ?? 'yolo',
        source: r?.source ?? 'default',
        args: r?.args ?? [],
      };
    });
  });

  // T1b / ASS-1742: per-project CLI context mode (clean isolated home vs the
  // operator's shared global state). Default CLEAN for reproducible coding
  // runs. Shared-only CLIs (Copilot, Gemini) render the dropdown disabled and
  // pinned to shared since they expose no config-home redirect.
  readonly cliContextModeResolved = signal<Record<string, { mode: string; source: string; supported: boolean }>>({});
  readonly cliContextModeAvailable = signal<readonly string[]>(['clean', 'shared']);
  /** Bound select value per CLI; mirrors the resolved effective context mode. */
  readonly cliContextModeDraft: Record<string, string> = {};
  /** Per-CLI write in flight; disables that row's controls until the PUT resolves. */
  readonly cliContextModeBusy: Record<string, boolean> = {};

  readonly cliContextModeMeta: Record<string, { label: string; hint: string }> = {
    'clean': { label: 'Clean', hint: 'Isolierter Per-Run-Home: der Run sieht nur Prompt + versionierte Repo-Dateien — reproduzierbar, ohne alte Session-/Memory-Reste.' },
    'shared': { label: 'Shared', hint: 'Der globale CLI-Zustand des Operators (Session-Historie, Memory, Settings). Nur bewusst waehlen.' },
  };
  readonly cliContextSourceMeta: Record<string, { label: string; hint: string }> = {
    'project': { label: 'project', hint: 'Explizit fuer dieses Projekt gesetzt — ueberschreibt den Plattform-Default.' },
    'default': { label: 'default', hint: 'Plattform-Default (clean) — kein Projekt-Override gesetzt.' },
  };

  cliContextModeLabel(mode: string | null | undefined) { return this.cliContextModeMeta[mode ?? '']?.label ?? mode ?? ''; }
  cliContextModeHint(mode: string | null | undefined) { return this.cliContextModeMeta[mode ?? '']?.hint ?? ''; }
  cliContextSourceLabel(source: string | null | undefined) { return this.cliContextSourceMeta[source ?? '']?.label ?? source ?? ''; }
  cliContextSourceHint(source: string | null | undefined) { return this.cliContextSourceMeta[source ?? '']?.hint ?? ''; }

  /** One row per CLI: effective context mode + source + whether clean is actually supported. */
  readonly cliContextModeRows = computed(() => {
    const resolved = this.cliContextModeResolved();
    return this.cliTypes.map((cli) => {
      const r = resolved[cli];
      return {
        cliType: cli as string,
        label: cliTypeLabel(cli as CliType),
        icon: cliTypeIcon(cli as CliType),
        mode: r?.mode ?? 'clean',
        source: r?.source ?? 'default',
        supported: r?.supported ?? false,
      };
    });
  });

  /**
   * Mode buttons. The four runner modes are: manual (off), auto-single
   * (run one then revert), auto-continuous (run all ready), paused
   * (deny auto-pickup but keep state). Click sends a PUT and refreshes.
   */
  readonly modes: readonly { id: string; label: string; tooltip: string }[] = [
    { id: 'manual', label: 'Manual', tooltip: 'Auto-pickup off; user starts each task.' },
    { id: 'auto-single', label: 'Auto · single', tooltip: 'Pick up the next ready task once, then revert to manual.' },
    { id: 'auto-continuous', label: 'Auto · continuous', tooltip: 'Pick up ready tasks continuously.' },
    { id: 'paused', label: 'Paused', tooltip: 'Hold all auto-pickup; manual starts still allowed.' }
  ];

  readonly orchModelOptions = computed(() => [
    { id: '', label: 'Default' },
    ...this.cliCatalog.modelsFor('claude')
      .filter(model => model.available !== false)
      .map(model => ({ id: model.id, label: model.isDefault ? `Default (${model.label})` : model.label })),
  ]);
  readonly autoPushOptions: readonly { id: AutoPushStrategy; label: string; tooltip: string; isDefault?: boolean }[] = [
    {
      id: 'never',
      label: 'Never',
      tooltip: 'Do not push automatically; the operator pushes manually.'
    },
    {
      id: 'on-completed',
      label: 'On completed',
      tooltip: 'Default. Push only after the job commit reaches 6-completed, after review.',
      isDefault: true
    },
    {
      id: 'always-immediate',
      label: 'Immediate',
      tooltip: 'Push right after auto-commit too. Higher rebase risk if review findings require rewriting local history.'
    }
  ];

  readonly effectiveMode = computed(() => {
    const status = this.runnerStatus();
    if (!status) return this.settings()?.runnerMode ?? 'manual';
    const proj = status.projects?.[this.projectName()];
    return proj?.mode ?? this.settings()?.runnerMode ?? 'manual';
  });

  readonly modeHint = computed(() => {
    switch (this.effectiveMode()) {
      case 'auto-continuous':
        return 'Auto-pickup is running. The runner will pick up the next ready task as soon as the current one finishes.';
      case 'auto-single':
        return 'After the next pickup, the runner reverts to Manual.';
      case 'paused':
        return 'Auto-pickup is held. Manual starts still work.';
      default:
        return 'No auto-pickup. You start each task manually.';
    }
  });

  readonly paths = computed(() => {
    return this.projectPaths() ?? {
      path: this.projectName(),
      rootPath: '',
      repositoryPath: ''
    };
  });

  readonly laneCounts = computed(() => {
    const grouped = this.grouped();
    if (!grouped) return [] as readonly { state: string; label: string; count: number }[];
    const proj = this.projectName();
    const c = (jobs: readonly { projectName: string }[]) => jobs.filter(j => j.projectName === proj).length;
    return [
      { state: TaskState.Backlog,     label: 'Backlog',     count: c(grouped.backlog ?? []) },
      { state: TaskState.Preparation, label: 'Preparation', count: c(grouped.preparation) },
      { state: TaskState.Ready,       label: 'Ready',       count: c(grouped.ready) },
      { state: TaskState.Progress,    label: 'Progress',    count: c(grouped.progress) },
      { state: TaskState.AutoReview, label: 'Post Processing', count: c(grouped.autoReview ?? grouped.review) },
      { state: TaskState.HumanReview, label: 'Review',      count: c(grouped.humanReview ?? []) },
      { state: TaskState.Escalated,   label: 'Escalated',  count: c(grouped.escalated ?? []) },
      { state: TaskState.Completed,   label: 'Delivered',   count: c(grouped.completed) },
      { state: TaskState.Archive,     label: 'Archive',     count: c(grouped.archive) }
    ];
  });

  readonly activeRunner = computed(() => this.runnerStatus()?.projects?.[this.projectName()] ?? null);

  readonly activeLaneLabel = computed(() => {
    const activeId = this.activeRunner()?.activeJobId;
    if (!activeId) return null;
    const grouped = this.grouped();
    if (!grouped) return null;
    const lanes: readonly [string, readonly { id: string }[]][] = [
      ['Backlog', grouped.backlog ?? []],
      ['Preparation', grouped.preparation ?? []],
      ['Ready', grouped.ready ?? []],
      ['Progress', grouped.progress ?? []],
      ['Post Processing', grouped.autoReview ?? grouped.review],
      ['Review', grouped.humanReview ?? []],
      ['Escalated', grouped.escalated ?? []],
      ['Delivered', grouped.completed],
      ['Archive', grouped.archive],
    ];
    return lanes.find(([, jobs]) => jobs.some(j => j.id === activeId))?.[0] ?? null;
  });

  queueHealthLabel(health: ProjectQueueHealth, emptyLabel: string, noun: string): string {
    if (health.issueCount === 0) return emptyLabel;
    return `${health.issueCount} ${noun}${health.issueCount === 1 ? '' : 's'}`;
  }

  readonly tokenTotalLabel = computed(() => {
    const entries = this.recentEntries();
    let input = 0, output = 0, count = 0;
    for (const e of entries) {
      if (!e.tokenUsage) continue;
      input += e.tokenUsage.inputTokens;
      output += e.tokenUsage.outputTokens;
      count++;
    }
    if (count === 0) return `${entries.length} entries; no orchestrator LLM calls yet.`;
    return `${entries.length} entries; ${count} orchestrator LLM call${count === 1 ? '' : 's'}: ↑${input.toLocaleString()} / ↓${output.toLocaleString()} tokens.`;
  });

  private pollTimer: VisibleIntervalHandle | null = null;

  ngOnInit(): void {
    this.cliCatalog.ensure('claude').subscribe({ error: () => void 0 });
    this.refreshAll();
    this.refreshCliModes();
    this.refreshCliContextModes();
    // Cycle 3: skip the 6-endpoint refreshAll fan-out when the panel is in
    // a backgrounded tab. The pre-Cycle-3 cadence put 42 requests over a
    // 10 s window from the project-detail panel alone (see logs/perf
    // baseline); this drops to zero when the tab is hidden.
    this.pollTimer = setVisibleInterval(() => this.refreshAll(true), 5_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refreshAll(silent = false): void {
    void silent;
    // Cycle 5: 1 round-trip instead of 6. /api/projects/{name}/snapshot
    // returns settings + runner-status + orchestrator-log-tail +
    // orchestrator-session + review-decisions-pending + runner-pending-
    // decisions all together. The standalone endpoints stay live for
    // other consumers (CLI usage sheet, board, etc.).
    this.jobService.getProjectSnapshot(this.projectName()).subscribe({
      next: (snap) => {
        const row = {
          autoCommit: snap.settings.autoCommit,
          autoPushStrategy: snap.settings.autoPushStrategy,
          runnerMode: snap.settings.runnerMode,
          orchestratorModel: snap.settings.orchestratorModel,
        };
        this.settings.set(row);
        if (this.autoCommitDraft !== row.autoCommit) this.autoCommitDraft = row.autoCommit;
        if (this.autoPushStrategyDraft !== row.autoPushStrategy) this.autoPushStrategyDraft = row.autoPushStrategy;
        const wantedModel = row.orchestratorModel ?? '';
        if (this.orchModelDraft !== wantedModel) this.orchModelDraft = wantedModel;

        // RunnerStatus signal expects the full RunnerStatus shape; the
        // snapshot only ships this project's slot, so wrap it. Other
        // projects' status comes from board polling and is irrelevant
        // here.
        if (snap.runnerStatus) {
          this.runnerStatus.set({ projects: { [this.projectName()]: snap.runnerStatus } });
        }

        this.recentEntries.set(snap.orchestratorLogTail ?? []);
        this.orchSession.set(snap.orchestratorSession ?? null);
        this.projectPaths.set(snap.paths ?? null);
        this.pendingDecisions.set(snap.reviewDecisionsPending ?? []);
        this.livePendingDecisions.set(snap.runnerPendingDecisions ?? []);
        this.queueHealth.set(snap.queueHealth ?? null);
      },
      error: () => { /* silent; keep last snapshot */ }
    });
    // Board feed stays separate (it covers all projects, not just this
    // one) and is owned by TaskService's own 2 s poll. We just nudge it.
    this.jobService.refresh(true);
    setTimeout(() => this.grouped.set(this.jobService.grouped()), 50);
  }

  /**
   * Send a reply to a live decision sentinel through the existing
   * /api/tasks/{jobId}/continue endpoint with mode 'steer'. The sentinel
   * resolves on the backend's next tick (the [user] log line cancels it),
   * which clears the banner without an explicit dismiss.
   */
  sendLiveDecisionReply(jobId: string): void {
    const text = (this.liveReplyDrafts[jobId] ?? '').trim();
    if (!text) return;
    this.liveReplySending[jobId] = true;
    this.liveReplyErrors[jobId] = null;
    this.jobService.continueJob(jobId, text, undefined, undefined, undefined, undefined, 'steer').subscribe({
      next: () => {
        this.liveReplyDrafts[jobId] = '';
        this.liveReplySending[jobId] = false;
        // Optimistically clear the banner; the next refresh tick will
        // re-confirm from the backend.
        this.livePendingDecisions.set(
          this.livePendingDecisions().filter(p => p.jobId !== jobId)
        );
        this.refreshAll(true);
      },
      error: (err) => {
        this.liveReplySending[jobId] = false;
        this.liveReplyErrors[jobId] = err?.error?.error || err?.message || 'Failed to send reply.';
      }
    });
  }

  setMode(mode: string): void {
    this.jobService.setRunnerMode(this.projectName(), mode).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  onAutoCommitChange(): void {
    this.jobService.setProjectAutoCommit(this.projectName(), this.autoCommitDraft).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  setAutoPushStrategy(strategy: AutoPushStrategy): void {
    if (this.autoPushStrategyDraft === strategy) return;
    this.autoPushStrategyDraft = strategy;
    this.jobService.setProjectAutoPushStrategy(this.projectName(), strategy).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  onOrchModelChange(): void {
    const model = this.orchModelDraft.trim();
    this.jobService.setProjectOrchestratorModel(this.projectName(), model || null).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  /**
   * Read the resolved per-CLI permission modes for this project. Fills the
   * dropdown drafts from the effective mode so an un-overridden CLI shows its
   * global/default posture rather than a blank control. Runs on panel open
   * and after each write.
   */
  private refreshCliModes(): void {
    this.jobService.getProjectCliModes(this.projectName()).subscribe({
      next: (res) => {
        this.cliModeResolved.set(res.resolved ?? {});
        if (res.available?.length) this.cliModeAvailable.set(res.available);
        for (const cli of this.cliTypes) {
          const resolved = res.resolved?.[cli]?.mode ?? 'yolo';
          if (this.cliModeDraft[cli] !== resolved) this.cliModeDraft[cli] = resolved;
        }
      },
      error: () => { /* keep last known modes */ }
    });
  }

  /**
   * Persist one CLI's permission mode as a project override, then refresh the
   * resolved map. Takes effect on the next spawn without a backend restart.
   */
  onCliModeChange(cli: string): void {
    const mode = this.cliModeDraft[cli] ?? 'yolo';
    this.cliModeBusy[cli] = true;
    this.jobService.setProjectCliMode(this.projectName(), cli, mode).subscribe({
      next: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); },
      error: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); }
    });
  }

  /** Clear a CLI's project override, reverting to global config / platform default. */
  resetCliMode(cli: string): void {
    this.cliModeBusy[cli] = true;
    this.jobService.setProjectCliMode(this.projectName(), cli, '').subscribe({
      next: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); },
      error: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); }
    });
  }

  /**
   * T1b / ASS-1742: read the resolved per-CLI context modes for this project.
   * Fills the dropdown drafts from the effective mode (default CLEAN) so an
   * un-overridden CLI shows its platform posture rather than a blank control.
   */
  private refreshCliContextModes(): void {
    this.jobService.getProjectCliContextModes(this.projectName()).subscribe({
      next: (res) => {
        this.cliContextModeResolved.set(res.resolved ?? {});
        if (res.available?.length) this.cliContextModeAvailable.set(res.available);
        for (const cli of this.cliTypes) {
          const resolved = res.resolved?.[cli]?.mode ?? 'clean';
          if (this.cliContextModeDraft[cli] !== resolved) this.cliContextModeDraft[cli] = resolved;
        }
      },
      error: () => { /* keep last known modes */ }
    });
  }

  /**
   * Persist one CLI's context mode as a project override, then refresh the
   * resolved map. Takes effect on the next spawn without a backend restart.
   */
  onCliContextModeChange(cli: string): void {
    const mode = this.cliContextModeDraft[cli] ?? 'clean';
    this.cliContextModeBusy[cli] = true;
    this.jobService.setProjectCliContextMode(this.projectName(), cli, mode).subscribe({
      next: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); },
      error: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); }
    });
  }

  /** Clear a CLI's context-mode project override, reverting to the platform default (CLEAN). */
  resetCliContextMode(cli: string): void {
    this.cliContextModeBusy[cli] = true;
    this.jobService.setProjectCliContextMode(this.projectName(), cli, '').subscribe({
      next: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); },
      error: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); }
    });
  }

<<<<<<< HEAD
  onStepEnabledChange(stepId: string, enabled: boolean): void {
    this.writeStep(stepId, { enabled });
  }

  onStepMove(stepId: string, direction: -1 | 1): void {
    if (this.pipelineOrderBusy()) return;
    const rows = this.orderedPipelineCatalogue();
    const index = rows.findIndex(step => step.id === stepId);
    if (index < 0) return;

    const section = this.pipelineOrderSection(rows[index]);
    if (section === 'core') return;

    let target = index + direction;
    while (target >= 0 && target < rows.length && this.pipelineOrderSection(rows[target]) !== section) {
      target += direction;
    }
    if (target < 0 || target >= rows.length) return;

    const next = [...rows];
    [next[index], next[target]] = [next[target], next[index]];
    const stepIds = next
      .filter(step => this.pipelineOrderSection(step) !== 'core')
      .map(step => step.id);

    this.pipelineOrderBusy.set(true);
    this.pipelineOrder.set(stepIds);
    this.jobService.setProjectPipelineStepOrder(this.projectName(), stepIds).subscribe({
      next: (res) => {
        this.pipelineOrderBusy.set(false);
        this.pipelineOrder.set(res.pipelineStepOrder ?? stepIds);
      },
      error: () => {
        this.pipelineOrderBusy.set(false);
        this.refreshPipelineOverrides();
      },
    });
  }

  onStepModeChange(stepId: string, mode: string): void {
    this.writeStep(stepId, { mode });
  }

  onStepPromptChange(stepId: string, prompt: string): void {
    this.writeStep(stepId, { prompt });
  }

  resetStepAgent(stepId: string): void {
    this.writeStep(stepId, {
      cliType: '',
      model: '',
      thinkingLevel: null,
    });
  }

  onStepAgentCommit(stepId: string, selection: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    this.writeStep(stepId, {
      cliType: selection.cliType,
      model: selection.model,
      thinkingLevel: selection.thinkingLevel,
    });
  }

  /**
   * The condition <select> changed. A non-value token (always / never /
   * on-abort / ...) persists immediately. A value-bearing token (task-type /
   * tag) only persists once a value exists - until then we keep a draft so the
   * value input appears. Picking the empty option clears the condition.
   */
  onStepConditionChange(stepId: string, when: string): void {
    const existingValue = this.pipelineConditionDraft()[stepId]?.value
      ?? this.pipelineOverrides()[stepId]?.condition?.value ?? '';

    if (!when) {
      this.setConditionDraft(stepId, { when: '', value: existingValue });
      this.writeStep(stepId, { condition: null });
      return;
    }

    if (PipelineStep_ConditionValueTokens.includes(when)) {
      this.setConditionDraft(stepId, { when, value: existingValue });
      if (existingValue.trim()) {
        this.writeStep(stepId, { condition: { when: when as PipelineStepConditionToken, value: existingValue.trim() } });
      }
      return;
    }

    this.setConditionDraft(stepId, { when, value: '' });
    this.writeStep(stepId, { condition: { when: when as PipelineStepConditionToken } });
  }

  /**
   * The condition value input is being typed (task-type / tag). Updates the
   * draft only so the field stays responsive; the write happens on commit
   * (blur / Enter) to avoid a PUT per keystroke.
   */
  onStepConditionValueInput(stepId: string, value: string): void {
    const when = this.pipelineConditionDraft()[stepId]?.when
      ?? this.pipelineOverrides()[stepId]?.condition?.when ?? '';
    this.setConditionDraft(stepId, { when, value });
  }

  /**
   * Commit the typed condition value (blur / Enter). Persists the token +
   * value; an empty value collapses the condition to null on the backend.
   */
  onStepConditionValueCommit(stepId: string): void {
    const draft = this.pipelineConditionDraft()[stepId];
    const ov = this.pipelineOverrides()[stepId];
    const when = draft?.when ?? ov?.condition?.when ?? '';
    const value = (draft?.value ?? ov?.condition?.value ?? '').trim();
    if (!when || !PipelineStep_ConditionValueTokens.includes(when)) return;
    this.writeStep(stepId, {
      condition: value ? { when: when as PipelineStepConditionToken, value } : null,
    });
  }

  private setConditionDraft(stepId: string, draft: { when: string; value: string }): void {
    this.pipelineConditionDraft.update(m => ({ ...m, [stepId]: draft }));
  }

  private clearConditionDraft(stepId: string): void {
    this.pipelineConditionDraft.update(m => {
      if (!(stepId in m)) return m;
      const next = { ...m };
      delete next[stepId];
      return next;
    });
  }

  /**
   * Merge one changed facet onto the step's current override and PUT the
   * whole step (the backend replaces the entry, so unchanged facets must
   * be resent). `enabled` is sent as null when the step is on so an
   * all-default step clears its entry instead of leaving a dead one.
   * Empty model/mode normalise to null = inherit the built-in default. A
   * `condition` of null clears it; undefined leaves the stored one untouched.
   */
  private writeStep(
    stepId: string,
    patch: {
      enabled?: boolean;
      cliType?: string;
      model?: string;
      thinkingLevel?: string | null;
      prompt?: string | null;
      mode?: string;
      condition?: PipelineStepCondition | null;
    },
  ): void {
    const cur = this.pipelineOverrides()[stepId] ?? {};
    const defaultEnabled = this.pipelineCatalogue().find(s => s.id === stepId)?.defaultEnabled ?? true;
    const enabled = patch.enabled ?? (cur.enabled ?? defaultEnabled);
    const model = (patch.model ?? cur.model ?? '').trim();
    const cliType = (patch.cliType ?? cur.cliType ?? '').trim();
    const thinkingLevel = (patch.thinkingLevel !== undefined ? patch.thinkingLevel : (cur.thinkingLevel ?? ''))?.trim() ?? '';
    const prompt = (patch.prompt !== undefined ? patch.prompt : (cur.prompt ?? ''))?.trim() ?? '';
    const mode = (patch.mode ?? cur.mode ?? '').trim();
    const condition = patch.condition !== undefined ? patch.condition : (cur.condition ?? null);

    this.pipelineStepBusy[stepId] = true;
    this.jobService.setProjectPipelineStep(this.projectName(), {
      stepId,
      // Only persist `enabled` when it differs from the step's built-in
      // default; otherwise send null so an at-default step does not leave a
      // dead override. This matters for opt-in steps (abort-review, drift)
      // whose default is off: enabling them must store true, not clear it.
      enabled: enabled === defaultEnabled ? null : enabled,
      cliType: cliType || null,
      model: model || null,
      thinkingLevel: thinkingLevel || null,
      prompt: prompt || null,
      mode: mode || null,
      condition: condition ?? null,
    }).subscribe({
      next: (res) => {
        this.pipelineStepBusy[stepId] = false;
        this.pipelineOverrides.set(res.pipelineSteps ?? {});
        this.clearConditionDraft(stepId);
      },
      error: () => {
        this.pipelineStepBusy[stepId] = false;
        // Re-read so the controls snap back to the persisted truth.
        this.refreshPipelineOverrides();
        this.clearConditionDraft(stepId);
      }
    });
  }

  asCliType(value: string | null | undefined): CliType | null {
    return value && (CLI_TYPES as readonly string[]).includes(value) ? value as CliType : null;
  }

  private phaseForStep(step: PipelineCatalogueStep): string {
    if (step.kind === 'aspect') return 'aspect';
    if (step.kind === 'tool') return 'tool';
    if (step.kind === 'drift') return 'drift';
    if (step.kind === 'core') return 'core';
    if (step.id.startsWith('pre-')) return 'pre';
    if (step.id.includes('decision')) return 'decision';
    if (step.id.includes('abort')) return 'abort';
    return 'post';
  }

  private orderedPipelineCatalogue(): readonly PipelineCatalogueStep[] {
    const steps = this.pipelineCatalogue();
    const pre = this.sortPipelineOrderSection(steps.filter(step => this.pipelineOrderSection(step) === 'pre'));
    const core = steps.filter(step => this.pipelineOrderSection(step) === 'core');
    const post = this.sortPipelineOrderSection(steps.filter(step => this.pipelineOrderSection(step) === 'post'));
    return [...pre, ...core, ...post];
  }

  private sortPipelineOrderSection(steps: readonly PipelineCatalogueStep[]): readonly PipelineCatalogueStep[] {
    const order = this.pipelineOrder();
    if (order.length === 0 || steps.length <= 1) return steps;

    const rank = new Map<string, number>();
    for (const id of order) {
      const key = id.trim().toLowerCase();
      if (key && !rank.has(key)) rank.set(key, rank.size);
    }
    if (rank.size === 0) return steps;

    return steps
      .map((step, index) => ({ step, index, rank: rank.get(step.id.toLowerCase()) ?? Number.MAX_SAFE_INTEGER }))
      .sort((a, b) => a.rank - b.rank || a.index - b.index)
      .map(x => x.step);
  }

  private pipelineOrderSection(step: PipelineCatalogueStep): 'pre' | 'core' | 'post' {
    if (step.kind === 'core') return 'core';
    if ((step.phase ?? this.phaseForStep(step)) === 'pre') return 'pre';
    return 'post';
  }

  private canMovePipelineStep(
    steps: readonly PipelineCatalogueStep[],
    index: number,
    direction: -1 | 1,
  ): boolean {
    const step = steps[index];
    if (!step) return false;
    const section = this.pipelineOrderSection(step);
    if (section === 'core') return false;

    let target = index + direction;
    while (target >= 0 && target < steps.length) {
      if (this.pipelineOrderSection(steps[target]) === section) return true;
      target += direction;
    }
    return false;
  }

  private pipelinePhaseLabel(phase: string): string {
    switch (phase) {
      case 'pre': return 'Pre';
      case 'core': return 'Core';
      case 'aspect': return 'Aspect reviews';
      case 'tool': return 'Tool steps';
      case 'decision': return 'Decision';
      case 'drift': return 'Drift';
      case 'abort': return 'Abort-only';
      default: return 'Post';
    }
  }

=======
  /** F35: persist one lane's sort strategy, then refresh the resolved map. */
  onLaneSortChange(lane: string): void {
    const strategy = this.laneSortDraft[lane] ?? 'manual';
    this.jobService.setLaneSortStrategy(this.projectName(), lane, strategy).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

>>>>>>> 545fc1ea (Nav-Umbau Schritt 3: Pipeline-Seite ueberarbeiten (Steps, Modelle, Prompt-Bindung, Kosten))
  repairQueueHealth(): void {
    if (this.queueRepairBusy()) return;
    this.queueRepairBusy.set(true);
    this.queueRepairMessage.set(null);
    this.jobService.repairProjectQueueHealth(this.projectName()).subscribe({
      next: (res) => {
        this.queueRepairBusy.set(false);
        this.queueHealth.set(res.queueHealth);
        this.queueRepairMessage.set(`Archived ${res.moved.length} folder${res.moved.length === 1 ? '' : 's'}.`);
        this.refreshAll(true);
      },
      error: (err) => {
        this.queueRepairBusy.set(false);
        this.queueRepairMessage.set(err?.error?.error || err?.message || 'Repair failed.');
      }
    });
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }
}
