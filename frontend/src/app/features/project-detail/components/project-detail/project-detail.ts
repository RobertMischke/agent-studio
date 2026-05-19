import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobService } from '../../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { GroupedJobs, ProjectQueueHealth, RunnerStatus } from '../../../../models/job.model';
import type { OrchestratorLogEntry, OrchestratorSession } from '../../../../features/orchestrator';
import { OrchestratorRunner_KnownModels } from './project-detail.models';
import { TokenSummaryBlockComponent } from '../../../../features/tokens';
import { GlobalOrchestratorCardComponent } from '../../../../features/orchestrator';
import { ProjectArchitectureSectionComponent } from '../project-architecture-section/project-architecture-section';
import { ProjectDriftSectionComponent } from '../project-drift-section/project-drift-section';
import { ProjectDriftOverviewSectionComponent } from '../project-drift-overview-section/project-drift-overview-section';
import { ProjectSupervisorSectionComponent } from '../project-supervisor-section/project-supervisor-section';
import { ProjectMetaCycleSectionComponent } from '../project-meta-cycle-section/project-meta-cycle-section';
import { ProjectAnalysisReportsSectionComponent } from '../project-analysis-reports-section/project-analysis-reports-section';
import { AutonomySliderComponent } from '../autonomy-slider/autonomy-slider';
import { AnalysisReport } from '../../../../models/analysis-report.model';

import { TooltipDirective } from '../../../../components/tooltip';
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

  private readonly jobService = inject(JobService);

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

  // ADR-0027: live, in-progress decision sentinels emitted by the running
  // job. Distinct from pendingDecisions (post-run, lane-scoped). Polled on
  // the same 5 s interval refreshAll uses; cleared by the backend the
  // moment the user replies (the [user] line resolves the sentinel).
  readonly livePendingDecisions = signal<readonly { jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }[]>([]);
  readonly liveReplyDrafts: Record<string, string> = {};
  readonly liveReplySending: Record<string, boolean> = {};
  readonly liveReplyErrors: Record<string, string | null> = {};

  // Two-way bound drafts so the form is responsive even before the
  // server round-trip completes.
  autoCommitDraft = false;
  autoPushStrategyDraft: AutoPushStrategy = 'on-completed';
  orchModelDraft = '';

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
      { state: '5-human-review',label: 'Human Review',count: c(grouped.humanReview ?? []) },
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
      ['Failed Pickup', grouped.failedPickup ?? []],
      ['Auto Review', grouped.autoReview ?? grouped.review],
      ['Human Review', grouped.humanReview ?? []],
      ['Completed', grouped.completed],
      ['Archive', grouped.archive],
    ];
    return lanes.find(([, jobs]) => jobs.some(j => j.id === activeId))?.[0] ?? null;
  });

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
          orchestratorModel: snap.settings.orchestratorModel
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
    // one) and is owned by JobService's own 2 s poll. We just nudge it.
    this.jobService.refresh(true);
    setTimeout(() => this.grouped.set(this.jobService.grouped()), 50);
  }

  /**
   * Send a reply to a live decision sentinel through the existing
   * /api/jobs/{jobId}/continue endpoint with mode 'steer'. The sentinel
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

  repairQueueHealth(): void {
    if (this.queueRepairBusy()) return;
    this.queueRepairBusy.set(true);
    this.queueRepairMessage.set(null);
    this.jobService.repairProjectQueueHealth(this.projectName()).subscribe({
      next: (res) => {
        this.queueRepairBusy.set(false);
        this.queueHealth.set(res.queueHealth);
        this.queueRepairMessage.set(`Moved ${res.moved.length} folder${res.moved.length === 1 ? '' : 's'} to Failed Pickup.`);
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
