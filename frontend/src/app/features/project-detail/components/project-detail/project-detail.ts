import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { GroupedJobs, ProjectQueueHealth, RunnerStatus } from '../../../../models/task.model';
import type { OrchestratorLogEntry, OrchestratorSession } from '../../../../features/orchestrator';
import { OrchestratorRunner_KnownModels, PipelineStep_KnownModels, PipelineStep_GateModes } from './project-detail.models';
import type { PipelineCatalogueStep, PipelineStepSetting } from '../../../../features/task-pipeline';
import { TokenSummaryBlockComponent } from '../../../../features/tokens';
import { GlobalOrchestratorCardComponent } from '../../../../features/orchestrator';
import { ProjectArchitectureSectionComponent } from '../project-architecture-section/project-architecture-section';
import { ProjectDriftSectionComponent } from '../project-drift-section/project-drift-section';
import { ProjectDriftOverviewSectionComponent } from '../project-drift-overview-section/project-drift-overview-section';
import { ProjectSupervisorSectionComponent } from '../project-supervisor-section/project-supervisor-section';
import { ProjectMetaCycleSectionComponent } from '../project-meta-cycle-section/project-meta-cycle-section';
import { ProjectAnalysisReportsSectionComponent } from '../project-analysis-reports-section/project-analysis-reports-section';
import { ProjectWorkspaceSectionComponent } from '../project-workspace-section/project-workspace-section';
import { AutonomySliderComponent } from '../autonomy-slider/autonomy-slider';
import { AnalysisReport } from '../../../../models/analysis-report.model';
import { TooltipDirective } from '../../../../components/tooltip';
import {
  SORTABLE_LANES,
  USER_VISIBLE_LANE_SORT_STRATEGIES,
  laneSortStrategyMeta,
} from '../../../../services/lane-sort.util';
interface ProjectSettingsRow {
  autoCommit: boolean;
  autoPushStrategy: AutoPushStrategy;
  runnerMode: string | null;
  orchestratorModel: string | null;
  laneSortStrategies: Record<string, string>;
}

type AutoPushStrategy = 'never' | 'on-completed' | 'always-immediate';

export type ProjectDetailView =
  | 'overview'
  | 'jobs'
  | 'settings'
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
    ProjectSupervisorSectionComponent,
    ProjectMetaCycleSectionComponent,
    ProjectAnalysisReportsSectionComponent,
    ProjectWorkspaceSectionComponent,
    AutonomySliderComponent, TooltipDirective],
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

  /** F35: per-lane sort-strategy selection, keyed by lane state. */
  readonly laneSortDraft: Record<string, string> = {};
  readonly sortableLanes = SORTABLE_LANES;
  readonly laneSortOptions = USER_VISIBLE_LANE_SORT_STRATEGIES;
  laneSortMeta(strategy: string | null | undefined) {
    return laneSortStrategyMeta(strategy);
  }

  // Pre/post pipeline-step config. The catalogue (which steps exist + what
  // each accepts) is project-independent and fetched once; the per-project
  // overrides come from the settings projection. Both feed pipelineRows().
  readonly pipelineCatalogue = signal<readonly PipelineCatalogueStep[]>([]);
  readonly pipelineOverrides = signal<Record<string, PipelineStepSetting>>({});
  readonly pipelineStepModels = PipelineStep_KnownModels;
  readonly pipelineGateModes = PipelineStep_GateModes;
  /** Per-step write in flight; disables that row's controls until the PUT resolves. */
  readonly pipelineStepBusy: Record<string, boolean> = {};

  /**
   * One row per configurable step: the catalogue metadata joined with the
   * project's current override. `enabled` defaults true (absent override or
   * null Enabled both mean on); `model` / `mode` empty string = inherit.
   */
  readonly pipelineRows = computed(() => {
    const overrides = this.pipelineOverrides();
    return this.pipelineCatalogue().map(step => {
      const ov = overrides[step.id];
      return {
        id: step.id,
        displayName: step.displayName,
        kind: step.kind,
        usesModel: step.usesModel,
        supportsMode: step.supportsMode,
        canDisable: step.canDisable,
        enabled: ov?.enabled !== false,
        model: ov?.model ?? '',
        mode: ov?.mode ?? '',
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

  readonly orchModelOptions = OrchestratorRunner_KnownModels;
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
      { state: '0-backlog',     label: 'Backlog',     count: c(grouped.backlog ?? []) },
      { state: '1-preparation', label: 'Preparation', count: c(grouped.preparation) },
      { state: '2-ready',       label: 'Ready',       count: c(grouped.ready) },
      { state: '3-progress',    label: 'Progress',    count: c(grouped.progress) },
      { state: '4-auto-review', label: 'Auto Review', count: c(grouped.autoReview ?? grouped.review) },
      { state: '5-human-review',label: 'Review',      count: c(grouped.humanReview ?? []) },
      { state: '6-completed',   label: 'Completed',   count: c(grouped.completed) },
      { state: '7-archive',     label: 'Archive',     count: c(grouped.archive) }
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
      ['Auto Review', grouped.autoReview ?? grouped.review],
      ['Review', grouped.humanReview ?? []],
      ['Completed', grouped.completed],
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
    this.refreshAll();
    this.loadPipelineConfig();
    // Cycle 3: skip the 6-endpoint refreshAll fan-out when the panel is in
    // a backgrounded tab. The pre-Cycle-3 cadence put 42 requests over a
    // 10 s window from the project-detail panel alone (see logs/perf
    // baseline); this drops to zero when the tab is hidden.
    this.pollTimer = setVisibleInterval(() => this.refreshAll(true), 5_000);
  }

  /**
   * Load the step catalogue (once) and this project's current per-step
   * overrides. The overrides ride on the settings projection rather than
   * the per-project snapshot, so this is a separate read; it is cheap and
   * runs on panel open plus after each write.
   */
  private loadPipelineConfig(): void {
    this.jobService.getPipelineCatalogue().subscribe({
      next: (cat) => this.pipelineCatalogue.set(cat.steps ?? []),
      error: () => { /* leave catalogue empty; the section just hides */ }
    });
    this.refreshPipelineOverrides();
  }

  private refreshPipelineOverrides(): void {
    const project = this.projectName();
    this.jobService.getAllProjectSettings().subscribe({
      next: (all) => this.pipelineOverrides.set(all[project]?.pipelineSteps ?? {}),
      error: () => { /* keep last known overrides */ }
    });
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
          laneSortStrategies: snap.settings.laneSortStrategies ?? {}
        };
        this.settings.set(row);
        if (this.autoCommitDraft !== row.autoCommit) this.autoCommitDraft = row.autoCommit;
        if (this.autoPushStrategyDraft !== row.autoPushStrategy) this.autoPushStrategyDraft = row.autoPushStrategy;
        const wantedModel = row.orchestratorModel ?? '';
        if (this.orchModelDraft !== wantedModel) this.orchModelDraft = wantedModel;
        for (const lane of this.sortableLanes) {
          const resolved = row.laneSortStrategies[lane.state] ?? 'manual';
          if (this.laneSortDraft[lane.state] !== resolved) this.laneSortDraft[lane.state] = resolved;
        }

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
    this.jobService.continueJob(jobId, text, undefined, undefined, undefined, 'steer').subscribe({
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

  /** F35: persist one lane's sort strategy, then refresh the resolved map. */
  onLaneSortChange(lane: string): void {
    const strategy = this.laneSortDraft[lane] ?? 'manual';
    this.jobService.setLaneSortStrategy(this.projectName(), lane, strategy).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  onStepEnabledChange(stepId: string, enabled: boolean): void {
    this.writeStep(stepId, { enabled });
  }

  onStepModelChange(stepId: string, model: string): void {
    this.writeStep(stepId, { model });
  }

  onStepModeChange(stepId: string, mode: string): void {
    this.writeStep(stepId, { mode });
  }

  /**
   * Merge one changed facet onto the step's current override and PUT the
   * whole step (the backend replaces the entry, so unchanged facets must
   * be resent). `enabled` is sent as null when the step is on so an
   * all-default step clears its entry instead of leaving a dead one.
   * Empty model/mode normalise to null = inherit the built-in default.
   */
  private writeStep(stepId: string, patch: { enabled?: boolean; model?: string; mode?: string }): void {
    const cur = this.pipelineOverrides()[stepId] ?? {};
    const enabled = patch.enabled ?? (cur.enabled !== false);
    const model = (patch.model ?? cur.model ?? '').trim();
    const mode = (patch.mode ?? cur.mode ?? '').trim();

    this.pipelineStepBusy[stepId] = true;
    this.jobService.setProjectPipelineStep(this.projectName(), {
      stepId,
      enabled: enabled ? null : false,
      model: model || null,
      mode: mode || null,
    }).subscribe({
      next: (res) => {
        this.pipelineStepBusy[stepId] = false;
        this.pipelineOverrides.set(res.pipelineSteps ?? {});
      },
      error: () => {
        this.pipelineStepBusy[stepId] = false;
        // Re-read so the controls snap back to the persisted truth.
        this.refreshPipelineOverrides();
      }
    });
  }

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
