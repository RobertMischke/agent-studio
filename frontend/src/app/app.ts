import { ChangeDetectionStrategy, Component, computed, effect, inject, OnInit, signal, untracked, ViewChild, ViewEncapsulation } from '@angular/core';
import { forkJoin } from 'rxjs';
import { FormsModule } from '@angular/forms';
import {
  ActiveFilterPill,
  BoardFiltersService,
  CreateJobDialogComponent,
  FiltersDropdownComponent,
  JobColumnComponent,
  KanbanFilterSidesheetComponent,
  LaneCollapseService,
  ProjectAutoInfo,
  ProjectTabsComponent,
  ProjectTokenChipInfo,
  TypeFilterOption,
  BoardMutationsService,
  CreateJobFormService,
  splitReadyByPhase,
} from './features/board';
import { JobDetailComponent, JobSelectionService, TriageController } from './features/job-detail';
import { CliUsageSheetComponent } from './features/cli';
import { OrchestratorSideSheetComponent } from './features/orchestrator';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  ProjectOverlaysComponent,
  ProjectOverlaysService,
  ProjectRailKey,
} from './features/project-detail';
import {
  AutoReviewIndicatorComponent,
  StatusBarComponent,
  UiPreferencesService,
  WorkspaceBannerComponent,
  WorkspaceOverlaysComponent,
  WorkspaceOverlaysService,
} from './features/shell';
import { E2ECleanupDialogComponent } from './features/dev-tools';
import {
  UpdateBannerComponent,
  UpdateBlockModalComponent,
  UpdateCenterComponent,
  UpdateVersionBadgeComponent,
} from './features/update';
import { VerboseDebugOverlayComponent } from './features/verbose-debug';
import { JobService } from './services/job.service';
import { ClientService } from './services/client.service';
import type { JobDetail, JobInfo, WatchPathEntry, CliType } from './models/job.model';
import { CLI_TYPES } from './models/job.model';
import type { CliModelInfo } from './features/cli';
import { ErrorDialogService } from './services/error-dialog.service';
import { cliTypeLabel as fmtCliTypeLabel, formatMultiplier as fmtMultiplier } from './services/format.util';
import { ErrorDialogComponent } from './components/error-dialog/error-dialog.component';
import { ConfirmDialogComponent } from './components/app-dialog/confirm-dialog.component';
import { NotificationStackComponent } from './components/app-dialog/notification-stack.component';
import { MediaLightboxComponent } from './components/media-lightbox/media-lightbox.component';
import { UpdateClientService } from './services/update.service';
import { projectIdentity } from './services/project-identity.util';
import { DevToolsService } from './services/dev-tools.service';
import { FeatureFlagsService } from './services/feature-flags.service';
import { JobCompletionSoundService } from './services/job-completion-sound.service';
import { TagRegistryStore } from './services/tag-registry.store';
import type { CliOutputLine } from './models/job.model';
import type { RunTimeline } from './features/run-timeline';
import type { JobScreenshot } from './features/screenshots';
import type { JobTokenSummary } from './features/tokens'; // verbose-debug overlay context types

interface VerboseDebugContext {
  lines: CliOutputLine[];
  runTimeline: RunTimeline | null;
  screenshots: JobScreenshot[];
  tokenSummary: JobTokenSummary | null;
  job: JobInfo | null;
}

@Component({
  selector: 'app-root',
  imports: [JobColumnComponent, JobDetailComponent, CliUsageSheetComponent, OrchestratorSideSheetComponent, ProjectOverlaysComponent, AutoReviewIndicatorComponent, StatusBarComponent, FormsModule, CreateJobDialogComponent, ErrorDialogComponent, ConfirmDialogComponent, NotificationStackComponent, MediaLightboxComponent, ProjectTabsComponent, E2ECleanupDialogComponent, WorkspaceOverlaysComponent, WorkspaceBannerComponent, UpdateBannerComponent, UpdateVersionBadgeComponent, UpdateCenterComponent, UpdateBlockModalComponent, VerboseDebugOverlayComponent, FiltersDropdownComponent, KanbanFilterSidesheetComponent],
  // Cycle 7b: OnPush. The shell mounts kanban + detail panel + many
  // sheets; default (Default) change detection re-checked the whole
  // tree on every async event (every poll tick, every signal write).
  // OnPush means CD only runs when an @Input changes (signals already
  // mark themselves dirty), or an event handler in the template fires.
  // The board sub-tree was already covered indirectly by JobCard's
  // OnPush; promoting the shell ensures the sibling sheets and the
  // header don't trigger whole-app passes during a 2 s grouped poll.
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Keep styles global to this subtree — the App shell still owns the
  // .header*, .filter-chip*, .overlay*, .create-dialog*, .error-dialog*
  // class rules used by the extracted dialogs and project-tabs.
  encapsulation: ViewEncapsulation.None,
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit {
  /**
   * Cycle 9j: selection state (selected detail, triage toast, lane
   * peers, URL sync, request-token guard) lives in JobSelectionService.
   * The shell re-exposes the read signals so existing template
   * bindings + keyboard guards keep working unchanged. The triage
   * HANDLERS (onTriageMove/Delete/Start, advanceToNextInLane) stay
   * here because they orchestrate JobService mutations + the
   * JobDetailComponent ViewChild (`clearTriageActing`).
   */
  private readonly jobSelection = inject(JobSelectionService);
  readonly selectedJob = this.jobSelection.selected;
  readonly triageToast = this.jobSelection.triageToast;

  @ViewChild('jobDetail') private jobDetailRef?: JobDetailComponent;
  @ViewChild('orchSideSheet') private orchSideSheetRef?: OrchestratorSideSheetComponent;
  /** Records the lane the user was triaging in. When the open job's state
   *  diverges from this (e.g. an external client moved it) we treat that as
   *  an auto-advance and toast accordingly. */
  private triageLaneState: string | null = null;
  /**
   * Read-only "Verbose Debug" overlay state opened from the orchestrator
   * side sheet's bug button. The protocol pane has its own copy that lives
   * inside the task chat workbench and reuses the live polling services;
   * this app-shell instance is the lazy-fetched escape hatch for the
   * project side sheet's "🐞" affordance, which is reachable even when the
   * task detail isn't currently displayed.
   */
  readonly verboseDebugContext = signal<VerboseDebugContext | null>(null);
  /**
   * Cycle 10a: create-job dialog state + open/cancel/submit logic
   * lives in CreateJobFormService. The shell re-exposes the visibility
   * signal + the bound fields via getters so the existing template
   * bindings keep working unchanged.
   */
  readonly createJobForm = inject(CreateJobFormService);
  /** Cycle 10b: board-mutation handlers (drag/drop, reorder, delete, archive, etc.) live here. */
  private readonly boardMutations = inject(BoardMutationsService);
  /** Re-exposed for the column template so the Archive-all button can disable
   *  itself + show a spinner while a bulk archive is in flight. */
  readonly archivingInProgress = this.boardMutations.archiving;
  /** Cycle 10c: triage panel + j/k navigation + auto-advance live here. */
  private readonly triage = inject(TriageController);
  readonly showCreate = this.createJobForm.visible;
  readonly availableModels = this.createJobForm.availableModels;
  /**
   * Cycle 9g: per-project overlay state (orch-feed / project-shell /
   * analysis-report) lives in ProjectOverlaysService.
   * The shell re-exposes the read signals so existing template guards +
   * keyboard guards work unchanged; the `<app-project-overlays />`
   * container owns the rendering.
   */
  private readonly projectOverlays = inject(ProjectOverlaysService);
  readonly orchFeedProject = this.projectOverlays.orchFeedProject;
  readonly projectShellName = this.projectOverlays.projectShellName;
  readonly projectShellRail = this.projectOverlays.projectShellRail;
  readonly analysisReportFocus = this.projectOverlays.analysisReportFocus;
  /**
   * Cycle 9g: workspace overlay state (tokens / screenshots / cli-admin)
   * lives in WorkspaceOverlaysService. The shell re-exposes the read
   * signals so the existing template guards keep working unchanged; the
   * `<app-workspace-overlays />` container owns the actual rendering.
   */
  private readonly workspaceOverlays = inject(WorkspaceOverlaysService);
  readonly workspaceTokensOpen = this.workspaceOverlays.tokensOpen;
  readonly workspaceScreenshotsOpen = this.workspaceOverlays.screenshotsOpen;
  readonly cliAdminOpen = this.workspaceOverlays.cliAdminOpen;
  private hashListener: (() => void) | null = null;
  private kanbanKeyListener: ((ev: KeyboardEvent) => void) | null = null;
  readonly watchPaths = signal<WatchPathEntry[]>([]);
  /**
   * Cycle 9 / ADR-0034: search query, four faceted filters, URL hash +
   * query-param round-trip, and `filteredGrouped` derivation all live
   * in BoardFiltersService (features/board/state/board-filters.service.ts).
   * The shell re-exposes the same signal/computed/method names so
   * existing template bindings keep working unchanged.
   */
  private readonly boardFilters = inject(BoardFiltersService);
  private readonly tagRegistryStore = inject(TagRegistryStore);
  readonly activeProjects = this.boardFilters.activeProjects;
  /** Active project names as a plain readonly array for the workspace banner input. */
  readonly bannerProjects = this.boardFilters.bannerProjects;
  // Cycle 9: side-sheet width owned by UiPreferencesService.
  private readonly uiPrefs = inject(UiPreferencesService);
  readonly sideSheetWidth = this.uiPrefs.sideSheetWidth;
  readonly collapsedGroups = signal<Set<string>>(new Set(JSON.parse(localStorage.getItem('collapsedGroups') ?? '[]')));
  /**
   * Per-lane collapse preference for the main board. Values are state ids
   * (`1-preparation` … `7-archive`); a state present here renders as a
   * narrow rail instead of a full column. Persisted in localStorage so the
   * user's layout survives reloads. Default is empty (everything expanded)
   * to keep the first-run board useful before any customisation.
   */
  /**
   * Cycle 9 / ADR-0034: lane collapse and container focus state live in
   * LaneCollapseService (features/board/state/lane-collapse.service.ts).
   * The shell exposes the same `collapsedLanes` and `focusedContainer`
   * signal references so existing template bindings and computeds keep
   * working unchanged. Methods further down delegate to the service.
   */
  private readonly laneCollapse = inject(LaneCollapseService);
  readonly collapsedLanes = this.laneCollapse.collapsedLanes;
  readonly focusedContainer = this.laneCollapse.focusedContainer;
  readonly taskNavCollapsed = this.uiPrefs.taskNavCollapsed;
  /**
   * Compact-card mode trades the full per-card metadata (model badge,
   * agent line, git pill, commit pill, last-activity line) for a dense
   * one-row title with a small CLI icon and a relative timestamp. Lets
   * the user fit many more cards on screen when they're scanning for a
   * task by name. Persisted across reloads.
   */
  readonly compactCards = this.uiPrefs.compactCards;
  readonly showE2ECleanup = signal(false);
  readonly devToolsMenuOpen = signal(false);
  /**
   * Free-text query for the kanban search box. Matched as a case-insensitive
   * substring across every JobInfo field that's loaded for the grouped view
   * (title, id, project, agent, model, CLI, session, state, owner, phase,
   * type, tag ids). Prompt-body text is intentionally not searched here -
   * grouped jobs don't carry their prompts, so a "matches body" pretence
   * would lie. Ephemeral; not persisted to localStorage.
   */
  // Cycle 9 / ADR-0034: filter state + URL sync delegated to BoardFiltersService.
  readonly searchQuery = this.boardFilters.searchQuery;
  readonly activeFilterCount = this.boardFilters.activeFilterCount;
  readonly hasActiveFiltersOrSearch = this.boardFilters.hasActiveFiltersOrSearch;
  readonly filteredGrouped = this.boardFilters.filteredGrouped;
  readonly filteredJobCount = this.boardFilters.filteredJobCount;
  readonly totalJobCount = this.boardFilters.totalJobCount;

  /**
   * VS Code-style filter sidesheet that hosts search + faceted filters +
   * visibility toggles in one place. Closed by default — the board uses
   * the full width unless the user actively opens it. Persisted across
   * reloads under `atp.kanban.filterSidesheetOpen` so a user who keeps it
   * open (a common power-user posture) doesn't have to reopen on every
   * reload.
   */
  readonly kanbanFilterSidesheetOpen = signal<boolean>(
    localStorage.getItem('atp.kanban.filterSidesheetOpen') === '1'
  );

  toggleKanbanFilterSidesheet(): void {
    const next = !this.kanbanFilterSidesheetOpen();
    this.kanbanFilterSidesheetOpen.set(next);
    localStorage.setItem('atp.kanban.filterSidesheetOpen', next ? '1' : '0');
  }

  closeKanbanFilterSidesheet(): void {
    this.kanbanFilterSidesheetOpen.set(false);
    localStorage.setItem('atp.kanban.filterSidesheetOpen', '0');
  }

  onSidesheetClearAll(): void {
    this.boardFilters.clearSearchAndFilters();
  }

  readonly projectNames = computed(() => {
    return this.watchPaths().map(wp => wp.name);
  });

  /**
   * ADR-0028: count of jobs in <c>3a-failed-pickup</c> across the visible
   * (filtered) board. Drives the persistent failure banner above the
   * dashboard. The lane itself is hide-when-empty; the banner is the
   * always-on cross-board surface that survives a collapsed lane and a
   * filtered owner view (counts respect the active project / client filter).
   */
  readonly failedPickupCount = computed(() => (this.filteredGrouped().failedPickup ?? []).length);

  /**
   * Banner click-through: scroll the failed-pickup lane into view and pulse
   * its outline so the user's eye lands on it. The lane is rendered inside
   * the same dashboard, so a smooth scroll plus a one-shot CSS class is
   * cheaper than a routing change.
   */
  scrollToFailedPickupLane(): void {
    // The failed-pickup lane lives inside the Active container. If the
    // user has focus-expanded another container, the lane element is not
    // in the DOM and a scroll target would be silently missing.
    if (this.focusedContainer() !== null && this.focusedContainer() !== 'active') {
      this.clearContainerFocus();
    }
    queueMicrotask(() => {
      const el = document.querySelector('[data-testid="lane-3a-failed-pickup"]') as HTMLElement | null;
      if (!el) return;
      el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
      el.classList.add('column--failed-pickup-pulse');
      setTimeout(() => el.classList.remove('column--failed-pickup-pulse'), 1400);
    });
  }

  // Cycle 9 / ADR-0034: filter signals + URL sync delegated to BoardFiltersService.
  // The shell re-exposes the same names so existing template bindings + call
  // sites keep working unchanged.
  readonly activeClientFilter = this.boardFilters.activeClientFilter;
  readonly activeTypeFilter = this.boardFilters.activeTypeFilter;
  readonly activeTagFilter = this.boardFilters.activeTagFilter;
  readonly activeType = this.boardFilters.activeType;
  readonly hasActiveFilters = this.boardFilters.hasActiveFilters;
  readonly activeFilterPills = this.boardFilters.activeFilterPills;
  /** Workspace tag registry, refreshed on init via `loadTagRegistry`. */
  readonly tagRegistry = this.tagRegistryStore.tags;
  readonly tagRegistryById = this.tagRegistryStore.byId;

  /** Static option list for the type filter dropdown. */
  readonly typeFilterOptions: readonly TypeFilterOption[] = [
    { value: 'bug', label: 'Bugs', icon: '🐞', kind: 'bug' },
    { value: 'feature', label: 'Features', icon: '✨', kind: 'feature' },
    { value: 'chore', label: 'Chores', icon: '·', kind: 'chore' },
  ];

  setClientFilter(id: string | null): void { this.boardFilters.setClientFilter(id); }
  clientFilterChange(event: Event): string | null { return this.boardFilters.clientFilterChange(event); }
  clearTypeFilters(): void { this.boardFilters.clearTypeFilters(); }
  onSetType(type: string | null): void { this.boardFilters.onSetType(type); }
  toggleTypeFilter(type: string): void { this.boardFilters.toggleTypeFilter(type); }
  toggleTagFilter(id: string): void { this.boardFilters.toggleTagFilter(id); }
  clearAllFilters(): void { this.boardFilters.clearAllFilters(); }
  removeFilterPill(pill: ActiveFilterPill): void { this.boardFilters.removeFilterPill(pill); }

  loadTagRegistry(): void {
    this.jobService.listTags().subscribe({
      next: tags => this.tagRegistryStore.set(tags),
      error: () => this.tagRegistryStore.set([]),
    });
  }

  // The visible lane order is the canonical Order field, which is also what
  // ProjectRunner.GetNextReadyJob picks by. Keeping a single source of truth
  // here means "what's at the top of Ready runs first" is structurally true,
  // not just usually true.
  readonly displayGrouped = computed(() => this.filteredGrouped());

  readonly focusGroups = computed(() => {
    const grouped = this.displayGrouped();
    // ADR-0025: seven lanes. The robot icon is the orchestrator's machine
    // pass; the eye icon is the user's "needs me" lane.
    // ADR-0026: 1a-orchestrator-prep is always rendered (rail at level 0);
    // 1b-needs-human-review is hide-when-empty.
    // Backlog-lane spec: 0-backlog leads the focus list when populated.
    const lanes = [
      { state: '0-backlog', title: 'Backlog', icon: '🗒️', jobs: grouped.backlog ?? [] },
      { state: '1-preparation', title: 'In Preparation', icon: '📋', jobs: grouped.preparation },
      { state: '1a-orchestrator-prep', title: 'Orch Prep', icon: '🤖', jobs: grouped.orchestratorPrep },
    ];
    if (grouped.needsHumanReview.length > 0) {
      lanes.push({ state: '1b-needs-human-review', title: 'Needs Clar', icon: '🚩', jobs: grouped.needsHumanReview });
    }
    lanes.push(
      { state: '2-ready', title: 'Ready', icon: '📦', jobs: grouped.ready },
      { state: '3-progress', title: 'In Progress', icon: '🔵', jobs: grouped.progress }
    );
    // ADR-0028: 3a-failed-pickup is hide-when-empty. The card style and the
    // amber outline live on the column component when state === '3a-failed-pickup'.
    if ((grouped.failedPickup ?? []).length > 0) {
      lanes.push({ state: '3a-failed-pickup', title: 'Failed Pickup', icon: '⚠️', jobs: grouped.failedPickup });
    }
    lanes.push(
      { state: '4-auto-review', title: 'Auto Review', icon: '🤖', jobs: grouped.autoReview },
      { state: '5-human-review', title: 'Human Review', icon: '👁️', jobs: grouped.humanReview },
      { state: '6-completed', title: 'Completed', icon: '🟢', jobs: grouped.completed },
      { state: '7-archive', title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] }
    );
    return lanes;
  });

  /**
   * Board lane groups. Three contiguous containers map the workflow:
   *
   *  - backlog: 0-backlog, 1-preparation, 1a-orchestrator-prep,
   *             1b-needs-human-review, 2-ready
   *  - active:  3-progress, 3a-failed-pickup, 4-auto-review
   *  - decide:  5-human-review, 6-completed, 7-archive ("Done & Decide" -
   *             the user-owned tail of the pipeline; sign-off plus the
   *             archive sit together because they all wait on the user.)
   *
   * The previous human/agent axis suffix was misleading (Backlog mixes
   * agent prep with human triage; Active sometimes pauses on
   * 3a-failed-pickup waiting for the user) and is removed.
   */
  readonly laneGroups = computed(() => {
    const grouped = this.displayGrouped();
    // ADR-0026: orchestrator-prep + needs-human-review join the backlog
    // bucket. The bounce lane only renders when at least one job lives there.
    // Backlog-lane spec: 0-backlog is the leftmost lane, the default landing
    // for new jobs and the active triage staging area.
    const backlogLanes: Array<{ state: string; title: string; icon: string; jobs: JobInfo[] }> = [
      { state: '0-backlog', title: 'Backlog', icon: '🗒️', jobs: grouped.backlog ?? [] },
      { state: '1-preparation', title: 'In Preparation', icon: '📋', jobs: grouped.preparation },
      { state: '1a-orchestrator-prep', title: 'Orch Prep', icon: '🤖', jobs: grouped.orchestratorPrep },
    ];
    if (grouped.needsHumanReview.length > 0) {
      backlogLanes.push({ state: '1b-needs-human-review', title: 'Needs Clar', icon: '🚩', jobs: grouped.needsHumanReview });
    }
    // ready-orchestrator-intake-lane: split 2-ready into Human Ready and
    // Orchestrator Intake. The Intake lane is hide-when-empty so projects
    // that have not opted into intake see the same single Ready column as
    // before. Both lanes carry the same `2-ready` filesystem state on
    // their data-state attribute so drag-and-drop / pickup keep working.
    const readySplit = splitReadyByPhase(grouped.ready);
    backlogLanes.push({ state: '2-ready', title: 'Human Ready', icon: '📦', jobs: readySplit.humanReady });
    if (readySplit.intake.length > 0) {
      backlogLanes.push({ state: '2-ready-intake', title: 'Orch Intake', icon: '🛂', jobs: readySplit.intake });
    }
    const activeLanes: Array<{ state: string; title: string; icon: string; jobs: JobInfo[] }> = [
      { state: '3-progress', title: 'In Progress', icon: '🔵', jobs: grouped.progress },
    ];
    // ADR-0028: 3a-failed-pickup is hide-when-empty.
    if ((grouped.failedPickup ?? []).length > 0) {
      activeLanes.push({ state: '3a-failed-pickup', title: 'Failed Pickup', icon: '⚠️', jobs: grouped.failedPickup });
    }
    activeLanes.push({ state: '4-auto-review', title: 'Auto Review', icon: '🤖', jobs: grouped.autoReview });
    return [
      {
        id: 'backlog',
        label: 'Backlog',
        lanes: backlogLanes
      },
      {
        id: 'active',
        label: 'Active',
        lanes: activeLanes
      },
      {
        id: 'decide',
        label: 'Done & Decide',
        lanes: [
          // ADR-0025: human-review waits on the user; it sits alongside
          // completed and archive in the user-owned tail.
          { state: '5-human-review', title: 'Human Review', icon: '👁️', jobs: grouped.humanReview },
          { state: '6-completed', title: 'Completed', icon: '🟢', jobs: grouped.completed },
          { state: '7-archive', title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] }
        ]
      }
    ];
  });
  readonly selectedJobUsesCopilot = computed(() => (this.selectedJob()?.info.cliType ?? 'copilot') === 'copilot');

  // Cycle 9j: triageLanePeers lives in JobSelectionService.
  readonly triageLanePeers = this.jobSelection.triageLanePeers;

  // Cycle 10a: form state (newTitle/newPrompt/newCliType/etc.) lives in
  // CreateJobFormService. Pass-through getters keep the existing
  // template `[(ngModel)]` and helper-method bindings working unchanged.
  readonly cliTypes = CLI_TYPES;

  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }
  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }
  onCreateCliTypeChange(t: CliType): void { this.createJobForm.onCreateCliTypeChange(t); }
  onDefaultCliChange(t: CliType): void { this.createJobForm.applyStoredCliDefault(); }
  onDefaultModelChange(ev: { cliType: CliType; model: string }): void { this.createJobForm.onDefaultModelChange(ev); }
  canAddTaskToGroup(state: string): boolean { return this.createJobForm.canAddTaskToGroup(state); }

  readonly devToolsFlags = computed(() => this.devTools.flags());

  onPickUpdateStable(): void {
    this.devToolsMenuOpen.set(false);
    this.updateClient.openCenter();
    void this.updateClient.refreshNow();
  }

  onPickDeleteE2E(): void {
    this.devToolsMenuOpen.set(false);
    this.showE2ECleanup.set(true);
  }

  onPickOrchestratorConfig(): void {
    this.devToolsMenuOpen.set(false);
    this.orchSideSheetRef?.show();
    this.orchSideSheetRef?.selectWindowMode('logic');
  }

  private toggleOrchestratorMode(mode: 'feed' | 'cli'): void {
    const sheet = this.orchSideSheetRef;
    if (!sheet) return;
    if (sheet.open() && sheet.mode() === mode) {
      sheet.hide();
      return;
    }
    sheet.show();
    sheet.selectWindowMode(mode);
  }

  constructor(
    readonly jobService: JobService,
    readonly errorDialog: ErrorDialogService,
    readonly devTools: DevToolsService,
    readonly clientService: ClientService,
    readonly featureFlags: FeatureFlagsService,
    private readonly _completionSound: JobCompletionSoundService,
    readonly updateClient: UpdateClientService,
  ) {
    // Cycle 10a: refresh the kanban after a successful create — the
    // CreateJobFormService doesn't call jobService.refresh itself
    // because that orchestration concern lives here.
    this.createJobForm.submitted$.subscribe(() => this.refresh());

    // Cycle 10c: bridge TriageController to the JobDetailComponent's
    // "acting" highlight via a closure so the ViewChild can resolve
    // lazily at call time. The closure is fine to register here because
    // it doesn't dereference jobDetailRef until invoked.
    this.triage.setClearActingCallback(() => this.jobDetailRef?.clearTriageActing());

    effect(() => {
      const selected = this.selectedJob();
      const jobs = this.jobService.jobs();

      if (!selected) {
        return;
      }

      const latest = jobs.find(job => job.jobKey === selected.info.jobKey);
      if (!latest) {
        return;
      }

      const currentExecution = selected.info.execution;
      const latestExecution = latest.execution;
      const executionChanged =
        (currentExecution?.status ?? null) !== (latestExecution?.status ?? null) ||
        (currentExecution?.runOutcome ?? null) !== (latestExecution?.runOutcome ?? null) ||
        (currentExecution?.processId ?? null) !== (latestExecution?.processId ?? null) ||
        (currentExecution?.exitCode ?? null) !== (latestExecution?.exitCode ?? null) ||
        (currentExecution?.durationSeconds ?? null) !== (latestExecution?.durationSeconds ?? null);

      if (selected.info.state === latest.state && !executionChanged) {
        return;
      }

      untracked(() => {
        // Token-guard the re-fetch: if the user (or an auto-advance after a
        // mutation) navigates to a different job while this request is in
        // flight, dropping the late response prevents the panel from
        // snapping back to the prior slug. Without this, a state-change
        // from the detail dropdown races advanceAfterMutation - the shell
        // re-fetches the just-moved job at the same time the pager wants
        // to land on the next slug, and whichever response arrives last
        // wins the `selectedJob` signal.
        const token = this.jobSelection.bumpOpenDetailToken();
        this.jobService.getDetail(latest.id, latest.watchPath).subscribe({
          next: (detail) => this.jobSelection.setSelectedFromAdvance(detail, token),
        });
      });
    });

    // Triage auto-advance on external move: when the open job's state
    // diverges from `triageLaneState` and we did NOT initiate the move
    // (no actingId in flight), treat it as if the user had hit "next" and
    // hop to the next peer in the original lane. The toast surfaces the
    // hand-off so the user knows what happened.
    effect(() => {
      const sel = this.selectedJob();
      const lane = this.jobSelection.triageLaneState;
      if (!sel || !lane) return;
      if (sel.info.state === lane) return;
      if (this.jobDetailRef?.triageActingId() != null) return;
      const peers = untracked(() => this.triageLanePeers());
      // The job no longer matches the lane it was being triaged in; advance.
      untracked(() => this.triage.advanceToNextInLane(lane, sel.info.jobKey, peers, /*external*/ true));
    });
  }

  ngOnInit() {
    // Backlog-lane spec: hydrate the filter bar from the URL hash before
    // rendering so a bookmark or copy-paste lands on the same view.
    this.boardFilters.hydrateFromUrl();
    this.loadTagRegistry();
    this.refresh();
    this.jobService.startLiveUpdates();
    this.jobService.getWatchPaths().subscribe({
      next: (entries) => {
        this.watchPaths.set(entries);
        if (entries.length > 0) this.createJobForm.newWatchPath = entries[0].path;
        // The deep-link hash listener can fire before watch paths are
        // known (e.g. on a hard reload of `#/projects/<slug>`); resolving
        // the slug → project name needs the watch-path list, so re-apply
        // once entries are available.
        this.applyProjectShellHash();
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to load projects',
          fallbackMessage: 'Failed to load projects',
          source: 'Project list'
        });
      },
    });
    this.jobService.refreshRunnerStatus();
    this.devTools.loadFlags();
    this.clientService.refresh();
    this.jobSelection.restoreFromUrl();

    // Deep-link: open the workspace token timeline when the URL already
    // points at it, and keep the overlay in sync as the hash changes.
    const applyHash = () => {
      this.workspaceOverlays.syncFromHash();
      this.applyProjectShellHash();
    };
    applyHash();
    this.hashListener = applyHash;
    window.addEventListener('hashchange', this.hashListener);

    // Keyboard shortcuts for kanban container focus-expand: 1/2/3 focus
    // the corresponding container, 0 exits focus. Suppressed while the
    // user is typing in an input/textarea/contenteditable and while a
    // detail/overlay is open (the kanban isn't visible then).
    this.kanbanKeyListener = (ev: KeyboardEvent) => {
      if (ev.defaultPrevented || ev.metaKey || ev.ctrlKey || ev.altKey) return;
      if (this.selectedJob() !== null) return;
      if (this.showCreate()) return;
      if (this.workspaceTokensOpen() || this.workspaceScreenshotsOpen()) return;
      if (this.projectShellName() !== null) return;
      const target = ev.target as HTMLElement | null;
      if (target) {
        const tag = target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
        if ((target as HTMLElement).isContentEditable) return;
      }
      const ids = this.laneGroups().map(g => g.id);
      if (ev.key === '1' && ids[0]) { this.toggleContainerFocus(ids[0]); ev.preventDefault(); return; }
      if (ev.key === '2' && ids[1]) { this.toggleContainerFocus(ids[1]); ev.preventDefault(); return; }
      if (ev.key === '3' && ids[2]) { this.toggleContainerFocus(ids[2]); ev.preventDefault(); return; }
      if (ev.key === '0') { this.clearContainerFocus(); ev.preventDefault(); return; }
    };
    window.addEventListener('keydown', this.kanbanKeyListener);
  }

  ngOnDestroy() {
    if (this.hashListener) {
      window.removeEventListener('hashchange', this.hashListener);
      this.hashListener = null;
    }
    if (this.kanbanKeyListener) {
      window.removeEventListener('keydown', this.kanbanKeyListener);
      this.kanbanKeyListener = null;
    }
  }

  onE2EDidDelete(): void {
    this.jobService.refresh(true);
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: JobInfo) { this.jobSelection.openDetail(job); }
  closeDetail() { this.jobSelection.closeDetail(); }
  isSelectedJob(job: JobInfo): boolean { return this.jobSelection.isSelected(job); }

  // Cycle 10c: triage panel + j/k navigation + auto-advance delegated
  // to TriageController. The shell forwards events from JobDetailComponent.
  onTriageMove(info: JobInfo, ev: { targetState: string; actionId: string }) { this.triage.move(info, ev); }
  onTriageMoveToTop(info: JobInfo, ev: { actionId: string }) { this.triage.moveToTop(info, ev); }
  onTriageDelete(info: JobInfo, ev: { actionId: string }) { this.triage.delete(info, ev); }
  onTriageStart(info: JobInfo, ev: { actionId: string }) { this.triage.start(info, ev); }
  onTriageNext(info: JobInfo) { this.triage.next(info); }
  onTriagePrev(info: JobInfo) { this.triage.prev(info); }
  onCompleteAndNextReview() { this.triage.completeAndNextReview(); }

  // Cycle 10b: board-mutation handlers delegate to BoardMutationsService.
  onJobDrop(event: { jobId: string; watchPath: string; targetState: string }) { this.boardMutations.moveJob(event); }
  onJobReorder(event: { state: string; jobs: { jobId: string; watchPath: string }[] }) { this.boardMutations.reorderJobs(event); }
  onDeleteFromBoard(job: JobInfo) { this.boardMutations.deleteFromBoard(job); }
  onDeleteFromDetail(info: JobInfo) { this.boardMutations.deleteFromDetail(info); }
  onStateChangeFromDetail(info: JobInfo, targetState: string) { this.boardMutations.changeStateFromDetail(info, targetState); }
  onArchiveAll() { this.boardMutations.archiveAllCompleted(this.filteredGrouped().completed); }

  openCreate(targetState?: string) {
    this.createJobForm.open({
      watchPaths: this.watchPaths(),
      activeProjects: this.activeProjects(),
      targetState,
    });
  }

  onSearchInput(event: Event) {
    const value = (event.target as HTMLInputElement).value;
    this.boardFilters.setSearchQuery(value);
  }

  setSearchQuery(value: string) {
    this.boardFilters.setSearchQuery(value);
  }

  clearSearch() {
    this.boardFilters.clearSearch();
  }

  /**
   * Whether the kanban board is the visible surface and the header search
   * icon should render. Hidden whenever any of the existing "is something
   * else open" signals are true: a task detail page, the project chat /
   * orchestrator side sheet, the update center, or one of the project
   * overlays. Mirrors the conditions the @if branches in the body already
   * use, so we don't introduce a parallel "current view" signal.
   *
   * `orchSideSheet.open()` is composed at the call site in the template
   * because the side sheet is a template ref, not an injected service.
   */
  readonly boardSearchVisible = computed(() => {
    if (this.selectedJob()) return false;
    if (this.projectShellName()) return false;
    if (this.analysisReportFocus()) return false;
    if (this.orchFeedProject()) return false;
    if (this.updateClient.centerOpen()) return false;
    return true;
  });

  /**
   * Toggle the orchestrator feed overlay. Picks the project to show by
   * preferring (1) the currently open detail's project, (2) the first
   * active project filter, (3) the first known watch path. Closes the
   * overlay if it is already open.
   */
  toggleOrchFeed(): void {
    this.toggleOrchestratorMode('feed');
  }

  closeOrchFeed(): void {
    this.orchFeedProject.set(null);
  }

  /** Tooltip for the toolbar button; shows which project the feed will open for. */
  orchFeedTooltip(): string {
    const project = this.pickOrchFeedProject();
    return project
      ? `Open orchestrator feed for "${project}"`
      : 'No project selected';
  }

  /**
   * Project the orchestrator side sheet should align to. Tracks the
   * currently open detail's project so flipping into a task and then
   * opening the side sheet picks the right thread automatically. When
   * no detail is open, falls back to the first active or first known
   * project the same way the feed overlay does.
   */
  readonly orchSideSheetPreferredProject = computed<string | null>(() => {
    const detail = this.selectedJob();
    if (detail?.info?.projectName) return detail.info.projectName;
    const active = [...this.activeProjects()];
    if (active.length > 0) return active[0];
    const watchPaths = this.watchPaths();
    return watchPaths.length > 0 ? watchPaths[0].name : null;
  });

  orchChatTooltip(): string {
    const project = this.orchSideSheetPreferredProject();
    return project
      ? `Toggle orchestrator chat for "${project}"`
      : 'No project selected';
  }

  /**
   * Phase 5: orchestrator side sheet emitted "make a task from this".
   * Picks the watch path that matches the named project, opens the
   * existing create-task dialog with the orchestrator reply seeded into
   * the prompt, and lets a short heuristic title fall out of the first
   * non-empty line.
   */
  /**
   * Phase: Verbose Debug. The orchestrator side sheet's "🐞" header button
   * fires this with the active task's id + watch path. We fetch the
   * evidence (cli output, run timeline, screenshots, plus the latest job
   * detail for token summary) in parallel and feed it to the shared
   * `<app-verbose-debug-overlay>`. The overlay is read-only; it never
   * mutates state and never starts a run.
   */
  onOpenVerboseDebugFromSheet(event: { jobId: string; watchPath: string; jobTitle: string | null }): void {
    forkJoin({
      detail: this.jobService.getDetail(event.jobId, event.watchPath),
      lines: this.jobService.getJobOutput(event.jobId, event.watchPath),
      runs: this.jobService.getRunTimeline(event.jobId, event.watchPath),
      screenshots: this.jobService.getJobScreenshots(event.jobId, event.watchPath)
    }).subscribe({
      next: ({ detail, lines, runs, screenshots }) => {
        this.verboseDebugContext.set({
          lines: lines ?? [],
          runTimeline: runs ?? null,
          screenshots: screenshots?.screenshots ?? [],
          tokenSummary: detail?.info?.tokenSummary ?? null,
          job: detail?.info ?? null
        });
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Verbose Debug failed to load',
          source: `task ${event.jobTitle ?? event.jobId}`
        });
      }
    });
  }

  closeVerboseDebug(): void {
    this.verboseDebugContext.set(null);
  }

  /**
   * Slice E: route a click on the bug-confirmation card's "Open task"
   * action to the kanban detail panel. Uses the same `getDetail` +
   * `selectedJob.set` flow as `openDetail` (and the URL-restore path)
   * so the task lands in the same focus view, with the URL synced for
   * deep-link reload, regardless of whether the new job has appeared
   * in the local kanban list yet.
   */
  onOpenJobDetailFromSheet(event: { jobId: string; watchPath: string }): void {
    history.replaceState(
      null,
      '',
      `?job=${encodeURIComponent(event.jobId)}&watchPath=${encodeURIComponent(event.watchPath)}`
    );
    const token = this.jobSelection.bumpOpenDetailToken();
    this.jobService.getDetail(event.jobId, event.watchPath).subscribe({
      next: (detail) => this.jobSelection.setSelectedFromAdvance(detail, token),
      error: (err) => {
        history.replaceState(null, '', window.location.pathname);
        this.errorDialog.show(err, {
          title: 'Failed to open task',
          source: `task ${event.jobId}`
        });
      }
    });
  }

  /**
   * Project Security panel "Create follow-up task" action (slice 1 of the
   * quality-system mockup). Opens the existing create-job dialog
   * pre-filled with the prompt body the panel composed from the most
   * recent review. The user picks model / target-state / title overrides
   * before submitting; the panel never queues a job behind the user's back.
   */
  onSecurityFollowUp(event: { projectName: string; prefill: string }): void {
    this.createJobForm.openSecurityFollowUp(event, this.watchPaths());
  }

  /**
   * "Open evidence" action: refresh the kanban so the freshly written
   * review is visible at the top of the history list. Slice 1 keeps this
   * deliberately minimal - a true file viewer overlay belongs in a later
   * slice. Today the project's `security/reviews/` folder is the canonical
   * pointer; the panel already shows the rel path next to each row.
   */
  onSecurityOpenEvidence(_event: { projectName: string; relPath: string }): void {
    // The relPath is rendered in the panel row itself; refresh the kanban
    // so a freshly-queued audit's eventual completion is visible without
    // a manual reload.
    this.refresh();
  }

  /** Refresh the kanban after a security audit was queued so the new job appears. */
  onSecurityAuditQueued(_event: { projectName: string; jobId: string }): void {
    this.refresh();
  }

  /**
   * Project UX/UI panel "Create follow-up task" / per-row "Task" action
   * (slice 6 of the quality-system mockup). Opens the existing create-job
   * dialog pre-filled with a prompt body the panel composed from the
   * council note or the design overview. The user picks model /
   * target-state / title before submitting.
   */
  onUxuiFollowUp(event: { projectName: string; prefill: string; title: string }): void {
    this.createJobForm.openUxuiFollowUp(event, this.watchPaths());
  }

  /** Refresh the kanban after a UX/UI design action was queued so the new job appears. */
  onUxuiActionQueued(_event: { projectName: string; action: string; jobId: string }): void {
    this.refresh();
  }

  onCreateTaskFromOrchestratorDraft(event: { projectName: string; promptText: string }): void {
    this.createJobForm.openOrchestratorDraftFollowUp(event, this.watchPaths());
  }

  private pickOrchFeedProject(): string | null {
    const detail = this.selectedJob();
    if (detail?.info?.projectName) return detail.info.projectName;
    const active = [...this.activeProjects()];
    if (active.length > 0) return active[0];
    const watchPaths = this.watchPaths();
    return watchPaths.length > 0 ? watchPaths[0].name : null;
  }

  // Cycle 9g: project-overlay open/close + URL-hash sync delegated to
  // ProjectOverlaysService. The shell keeps thin pass-through methods
  // because external entry points (project-tabs, kanban project chip)
  // still go through it.
  openProjectShell(name: string, rail: ProjectRailKey = DEFAULT_PROJECT_RAIL_KEY): void {
    this.projectOverlays.openProjectShell(name, rail, this.watchPaths());
  }
  closeProjectShell(): void { this.projectOverlays.closeProjectShell(); }
  openAnalysisReport(project: string, reportId: string): void {
    this.projectOverlays.openAnalysisReport(project, reportId);
  }
  closeAnalysisReport(): void { this.projectOverlays.closeAnalysisReport(); }
  private applyProjectShellHash(): void {
    this.projectOverlays.syncShellFromHash(this.watchPaths());
  }

  // Cycle 9g: workspace overlay open/close + URL-hash sync delegated to
  // WorkspaceOverlaysService. The shell keeps these thin pass-throughs
  // because external call sites (status bar, usage hover panel, dev-tools
  // menu, screenshot reel) and deep-link entry points still go through
  // the shell.
  openWorkspaceTokens(): void { this.workspaceOverlays.openTokens(); }
  closeWorkspaceTokens(): void { this.workspaceOverlays.closeTokens(); }
  openWorkspaceScreenshots(): void { this.workspaceOverlays.openScreenshots(); }
  closeWorkspaceScreenshots(): void { this.workspaceOverlays.closeScreenshots(); }
  toggleWorkspaceScreenshots(): void { this.workspaceOverlays.toggleScreenshots(); }
  openCliAdmin(): void { this.workspaceOverlays.openCliAdmin(); }
  closeCliAdmin(): void { this.workspaceOverlays.closeCliAdmin(); }
  toggleCliAdmin(): void {
    this.toggleOrchestratorMode('cli');
  }

  /**
   * "Open task" link inside the workspace reel lightbox: close the
   * reel overlay, navigate the side panel to the screenshot's
   * originating job. Mirrors the open-task pattern used by the
   * orchestrator feed.
   */
  onOpenTaskFromReel(s: JobScreenshot): void {
    this.closeWorkspaceScreenshots();
    if (!s?.jobId || !s?.watchPath) return;
    history.replaceState(null, '', `?job=${encodeURIComponent(s.jobId)}&watchPath=${encodeURIComponent(s.watchPath)}`);
    this.jobService.getDetail(s.jobId, s.watchPath).subscribe({
      next: (detail) => this.selectedJob.set(detail),
      error: () => { /* keep the user where they were */ }
    });
  }

  cancelCreate() { this.createJobForm.cancel(); }
  submitCreate() { this.createJobForm.submit(); }

  toggleProject(event: { name: string; additive: boolean } | string) {
    if (typeof event === 'string') {
      // Legacy call sites (programmatic invocations) keep their toggle
      // semantics so multi-select remains reachable without a modifier key.
      this.boardFilters.toggleProject(event);
      return;
    }
    this.boardFilters.selectProject(event.name, event.additive);
  }
  isProjectActive(name: string): boolean { return this.boardFilters.isProjectActive(name); }

  // Pre-bound arrow-function aliases for child components that take a
  // predicate-style input (e.g. <app-project-tabs>). Using arrows keeps
  // `this` correct without per-call .bind().
  readonly isProjectActiveFn = (name: string) => this.isProjectActive(name);
  readonly getRunnerIndicatorFn = (name: string) => this.getRunnerIndicator(name);
  readonly getAutoInfoFn = (name: string) => this.getAutoInfo(name);
  readonly getProjectTokenChipFn = (name: string) => this.getProjectTokenChip(name);
  readonly identityFor = (name: string) => projectIdentity(name);

  /**
   * Aggregates `JobInfo.tokenSummary` across every job for `name` so the
   * project chip on the board can show total tokens without an extra
   * round-trip. Returns null when the project has no tokens at all - the
   * chip stays clean for AI-untouched projects. The tooltip (hover) and
   * the badge label use the same `formatTokens` shorthand the per-card
   * popover uses, so totals read consistently across the surfaces.
   */
  private getProjectTokenChip(name: string): ProjectTokenChipInfo | null {
    const jobs = this.jobService.jobs();
    let totalTokens = 0;
    let inputTokens = 0;
    let outputTokens = 0;
    let cacheReadTokens = 0;
    let cacheCreationTokens = 0;
    let jobsWithTokens = 0;
    const modelLastSeen = new Map<string, number>();
    for (const j of jobs) {
      if (j.projectName !== name) continue;
      const ts = j.tokenSummary;
      if (!ts || ts.totalTokens <= 0) continue;
      jobsWithTokens++;
      totalTokens += ts.totalTokens;
      inputTokens += ts.inputTokens;
      outputTokens += ts.outputTokens;
      cacheReadTokens += ts.cacheReadTokens;
      cacheCreationTokens += ts.cacheCreationTokens;
      // Walk per-call entries so we capture model switches (meta-tasks),
      // not just the last model. We rank by the entry timestamp so the
      // most recently used model lands first in the chip tooltip.
      for (const e of ts.entries ?? []) {
        const m = (e.model ?? '').trim();
        if (!m) continue;
        const t = Date.parse(e.ts) || 0;
        const prev = modelLastSeen.get(m) ?? 0;
        if (t > prev) modelLastSeen.set(m, t);
      }
      if (ts.lastModel) {
        const m = ts.lastModel.trim();
        if (m) {
          const t = Date.parse(ts.lastUpdate ?? '') || 0;
          const prev = modelLastSeen.get(m) ?? 0;
          if (t > prev) modelLastSeen.set(m, t);
        }
      }
    }
    if (totalTokens <= 0 || jobsWithTokens === 0) return null;
    const models = [...modelLastSeen.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([m]) => m);
    const fmt = this.formatTokensCompact;
    const tooltipParts: string[] = [
      `↑ ${fmt(inputTokens)} input · ↓ ${fmt(outputTokens)} output`
    ];
    if (cacheReadTokens > 0) tooltipParts.push(`⚡ ${fmt(cacheReadTokens)} cache read`);
    if (cacheCreationTokens > 0) tooltipParts.push(`+ ${fmt(cacheCreationTokens)} cache write`);
    tooltipParts.push(`${jobsWithTokens} ${jobsWithTokens === 1 ? 'task' : 'tasks'} with AI activity`);
    if (models.length > 0) tooltipParts.push(`Models: ${models.join(', ')}`);
    return {
      totalTokens,
      inputTokens,
      outputTokens,
      cacheReadTokens,
      cacheCreationTokens,
      jobsWithTokens,
      models,
      label: fmt(totalTokens),
      tooltip: tooltipParts.join('\n')
    };
  }

  /** Same shorthand as `JobCardComponent.formatTokens` - keeps surfaces consistent. */
  private formatTokensCompact = (n: number): string => {
    if (!Number.isFinite(n) || n <= 0) return '0';
    if (n < 1_000) return Math.round(n).toString();
    if (n < 10_000) return (n / 1_000).toFixed(1) + 'k';
    if (n < 1_000_000) return Math.round(n / 1_000) + 'k';
    if (n < 10_000_000) return (n / 1_000_000).toFixed(1) + 'M';
    return Math.round(n / 1_000_000) + 'M';
  };

  getRunnerIndicator(name: string): { icon: string; cls: string } | null {
    const status = this.jobService.runnerStatus();
    const runner = status.projects[name];
    if (!runner) return null;
    if (runner.activeJobId) return { icon: '🔵', cls: 'running' };
    if (runner.mode === 'paused') return { icon: '⏸', cls: 'paused' };
    if (runner.mode === 'auto-continuous') return { icon: '🟢', cls: 'idle' };
    if (runner.mode === 'auto-single') return { icon: '🟢', cls: 'idle' };
    return null;
  }

  getAutoInfo(name: string): ProjectAutoInfo {
    const status = this.jobService.runnerStatus();
    const runner = status.projects[name];
    const mode = runner?.mode ?? 'manual';
    const readyCount = runner?.queuedJobIds.length ?? 0;
    const hasActive = !!runner?.activeJobId;

    if (mode === 'auto-continuous' || mode === 'auto-single') {
      return {
        state: 'on',
        readyCount,
        icon: '🔁',
        label: 'Auto',
        tooltip: readyCount > 0
          ? `Auto-pickup is on — when the current task finishes, the next Ready task starts automatically (${readyCount} waiting). Click to stop; the running task will continue, but no further tasks will be picked up.`
          : `Auto-pickup is on — the next task moved to Ready will start automatically. Click to stop; the running task (if any) will continue but no further tasks will be picked up.`
      };
    }

    if (mode === 'paused' && hasActive) {
      return {
        state: 'stopping',
        readyCount,
        icon: '⏸',
        label: 'Stopping',
        tooltip: `Auto-pickup stopped — the current task keeps running, but no more tasks will be picked up automatically. Click to resume auto-pickup.`
      };
    }

    return {
      state: 'off',
      readyCount,
      icon: '▶',
      label: 'Auto',
      tooltip: readyCount > 0
        ? `Enable auto-pickup — when the current task finishes, the next Ready task starts automatically (${readyCount} waiting).`
        : `Enable auto-pickup — as soon as a task moves to Ready, it will start automatically.`
    };
  }

  onToggleAuto(name: string) {
    const runner = this.jobService.runnerStatus().projects[name];
    const mode = runner?.mode ?? 'manual';
    const newMode = (mode === 'auto-continuous' || mode === 'auto-single') ? 'paused' : 'auto-continuous';
    this.jobService.setRunnerMode(name, newMode).subscribe({
      next: () => this.jobService.refreshRunnerStatus(true),
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to change auto-pickup mode',
          fallbackMessage: 'Failed to change auto-pickup mode',
          source: `Project ${name}`
        });
      }
    });
  }

  onFileSaved() { this.boardMutations.refreshAfterFileSave(); }
  onProjectChanged(targetWatchPath: string) { this.boardMutations.reopenAfterProjectChange(targetWatchPath); }

  closeErrorDialog() {
    this.errorDialog.close();
  }

  copyErrorDetails() {
    this.errorDialog.copyActiveError();
  }

  copyErrorButtonLabel(): string {
    switch (this.errorDialog.copyState()) {
      case 'copied':
        return 'Copied';
      case 'failed':
        return 'Copy failed';
      default:
        return 'Copy output';
    }
  }

  openCliConfigFromError() {
    if (!this.selectedJobUsesCopilot()) return;
    this.errorDialog.requestCliConfig();
  }

  // Side-sheet width and collapse functionality
  toggleGroupCollapse(state: string) {
    const current = new Set(this.collapsedGroups());
    if (current.has(state)) {
      current.delete(state);
    } else {
      current.add(state);
    }
    this.collapsedGroups.set(current);
    localStorage.setItem('collapsedGroups', JSON.stringify([...current]));
  }

  isGroupCollapsed(state: string): boolean {
    return this.collapsedGroups().has(state);
  }

  // Cycle 9: per-lane collapse + container focus methods delegate to LaneCollapseService.
  // The shell forwards the lane-id list so the service stays free of
  // the kanban catalogue shape; everything else is straight pass-through.

  toggleLaneCollapse(state: string): void { this.laneCollapse.toggleLaneCollapse(state); }
  isLaneCollapsed(state: string): boolean { return this.laneCollapse.isLaneCollapsed(state); }
  expandedLaneCount(group: { lanes: Array<{ state: string }> }): number {
    return this.laneCollapse.expandedLaneCount(group);
  }
  isContainerFocused(id: string): boolean { return this.laneCollapse.isContainerFocused(id); }
  toggleContainerFocus(id: string): void {
    this.laneCollapse.toggleContainerFocus(id, this.laneGroups().map(g => g.id));
  }
  clearContainerFocus(): void { this.laneCollapse.clearContainerFocus(); }

  // Cycle 9: UI-pref methods delegate to UiPreferencesService.
  setTaskNavCollapsed(collapsed: boolean): void { this.uiPrefs.setTaskNavCollapsed(collapsed); }
  toggleCompactCards(): void { this.uiPrefs.toggleCompactCards(); }
  startResize(event: MouseEvent): void { this.uiPrefs.startResize(event); }

}
