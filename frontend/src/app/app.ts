import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  OnInit,
  signal,
  untracked,
  viewChild,
  ViewChild,
  ViewEncapsulation,
  OnDestroy,
} from '@angular/core';
import { forkJoin } from 'rxjs';
import { FormsModule } from '@angular/forms';
import {
  BoardFiltersService,
  CreateJobDialogComponent,
  FiltersDropdownComponent,
  JobColumnComponent,
  KanbanFilterSidesheetComponent,
  LaneCollapseService,
  ProjectTabsComponent,
  TypeFilterOption,
  BoardMutationsService,
  CreateJobFormService,
  buildProjectTokenChip,
  projectAutoInfo,
  projectRunnerIndicator,
  splitReadyByPhase,
} from './features/board';
import {
  JobDetailComponent,
  JobSelectionService,
  TriageController,
  overflowActionsFor,
  primaryActionFor,
  type TriageButton,
} from './features/job-detail';
import { CliUsageSheetComponent } from './features/cli';
import {
  OrchestratorSettingsModalComponent,
  OrchestratorSideSheetComponent,
} from './features/orchestrator';
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
  WorkspaceCreateDialogComponent,
  WorkspaceManagerService,
  WorkspaceOverlaysComponent,
  WorkspaceOverlaysService,
} from './features/shell';
import { E2ECleanupDialogComponent } from './features/dev-tools';
import {
  UpdateBlockModalComponent,
  UpdateCenterComponent,
  UpdateVersionBadgeComponent,
} from './features/update';
// UpdateBannerComponent removed in F56; update notifications now flow through
// UpdateNotificationBridge → NotificationService → notification-stack toasts.
import { VerboseDebugOverlayComponent } from './features/verbose-debug';
import {
  StudioShellComponent,
  ProjectHubViewComponent,
  StudioDiffViewComponent,
  StudioActivityViewComponent,
  StudioTabStateService,
  StudioPanelStateService,
} from './features/studio-shell';
import { JobService } from './services/task.service';
import { ClientService } from './services/client.service';
import { NotificationService } from './services/notification.service';
import type { JobInfo, WatchPathEntry, CliType } from './models/task.model';
import { CLI_TYPES } from './models/task.model';
import { ErrorDialogService } from './services/error-dialog.service';
import {
  cliTypeLabel as fmtCliTypeLabel,
  formatMultiplier as fmtMultiplier,
} from './services/format.util';
import { ErrorDialogComponent } from './components/error-dialog/error-dialog.component';
import { ConfirmDialogComponent } from './components/app-dialog/confirm-dialog/confirm-dialog.component';
import { StudioIconComponent } from './components/studio-icon/studio-icon.component';
import { NotificationStackComponent } from './components/app-dialog/notification-stack/notification-stack.component';
import { MediaLightboxComponent } from './components/media-lightbox/media-lightbox.component';
import { UpdateClientService } from './services/update.service';
import { UpdateNotificationBridge } from './services/update-notification-bridge.service';
import { projectIdentity } from './services/project-identity.util';
import { DevToolsService } from './services/dev-tools.service';
import { FeatureFlagsService } from './services/feature-flags.service';
import { JobCompletionSoundService } from './services/task-completion-sound.service';
import { TagRegistryStore } from './services/tag-registry.store';
import { CliCatalogStore } from './services/cli-catalog.store';
import type { CliOutputLine } from './models/task.model';
import type { RunTimeline } from './features/run-timeline';
import type { JobScreenshot } from './features/screenshots';
import { TooltipDirective } from './components/tooltip';
import { MenuComponent, MenuItem, MenuItemClickEvent } from './components/menu';
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
  imports: [
    JobColumnComponent,
    JobDetailComponent,
    CliUsageSheetComponent,
    OrchestratorSideSheetComponent,
    OrchestratorSettingsModalComponent,
    ProjectOverlaysComponent,
    AutoReviewIndicatorComponent,
    StatusBarComponent,
    FormsModule,
    CreateJobDialogComponent,
    ErrorDialogComponent,
    ConfirmDialogComponent,
    NotificationStackComponent,
    MediaLightboxComponent,
    ProjectTabsComponent,
    E2ECleanupDialogComponent,
    WorkspaceOverlaysComponent,
    WorkspaceBannerComponent,
    WorkspaceCreateDialogComponent,
    UpdateVersionBadgeComponent,
    UpdateCenterComponent,
    UpdateBlockModalComponent,
    VerboseDebugOverlayComponent,
    FiltersDropdownComponent,
    KanbanFilterSidesheetComponent,
    TooltipDirective,
    MenuComponent,
    StudioShellComponent,
    ProjectHubViewComponent,
    StudioDiffViewComponent,
    StudioActivityViewComponent,
    StudioIconComponent,
  ],
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
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  readonly jobService = inject(JobService);
  readonly errorDialog = inject(ErrorDialogService);
  readonly devTools = inject(DevToolsService);
  readonly clientService = inject(ClientService);
  private readonly notifications = inject(NotificationService);
  readonly featureFlags = inject(FeatureFlagsService);
  private readonly _completionSound = inject(JobCompletionSoundService);
  readonly updateClient = inject(UpdateClientService);
  private readonly _updateBridge = inject(UpdateNotificationBridge);
  readonly studioTabState = inject(StudioTabStateService);
  private readonly studioPanelState = inject(StudioPanelStateService);

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
  /**
   * Signal-form view child for the orchestrator side-sheet. Lets the
   * `effectiveCompactCards` computed react when the rail opens/closes
   * (F4) without a manual subscription bridge. The legacy @ViewChild
   * above stays for the imperative `toggle()` call sites.
   */
  private readonly orchSideSheetSig = viewChild<OrchestratorSideSheetComponent>('orchSideSheet');
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
   * Orchestrator Settings modal visibility. Replaces the former "Logic" tab
   * inside the sidesheet; the modal uses the project-shell rail + panel
   * layout so settings sit visually alongside the project window pattern.
   */
  readonly orchestratorSettingsOpen = signal(false);
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
  /**
   * Owns the create-workspace modal visibility. Public so the template
   * can bind <code>workspaceManager.createOpen()</code> for the
   * <code>@if</code> guard around <code>&lt;app-workspace-create-dialog&gt;</code>;
   * the studio-shell calls into it via <code>openCreate()</code> when
   * the "+ Add workspace" affordance fires.
   */
  readonly workspaceManager = inject(WorkspaceManagerService);
  readonly workspaceTokensOpen = this.workspaceOverlays.tokensOpen;
  readonly workspaceScreenshotsOpen = this.workspaceOverlays.screenshotsOpen;
  readonly cliAdminOpen = this.workspaceOverlays.cliAdminOpen;
  private hashListener: (() => void) | null = null;
  private kanbanKeyListener: ((ev: KeyboardEvent) => void) | null = null;
  private boardShortcutListener: ((ev: KeyboardEvent) => void) | null = null;
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
  private readonly cliCatalogStore = inject(CliCatalogStore);
  readonly activeProjects = this.boardFilters.activeProjects;
  /** Active project names as a plain readonly array for the workspace banner input. */
  readonly bannerProjects = this.boardFilters.bannerProjects;
  // Cycle 9: side-sheet width owned by UiPreferencesService.
  private readonly uiPrefs = inject(UiPreferencesService);
  readonly sideSheetWidth = this.uiPrefs.sideSheetWidth;
  readonly collapsedGroups = signal<Set<string>>(
    new Set(JSON.parse(localStorage.getItem('collapsedGroups') ?? '[]')),
  );
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

  /**
   * F4: effective compact mode for board cards. The user's persisted
   * `compactCards` preference still controls the default; when the
   * orchestrator rail is open, we force-engage compact rendering so
   * the lanes don't clip behind the 640 px panel. Closing the rail
   * reverts to the persisted preference automatically.
   *
   * F43: the rail-forced compact rule yields when the user explicitly
   * toggles to "Full" while the rail is open (`userOverridesCompactWhileRail`).
   * The override clears the next time the rail closes (see the
   * `clearCompactOverrideOnRailClose` effect below) so re-opening the
   * rail re-engages the auto-compact rule.
   */
  readonly effectiveCompactCards = computed<boolean>(() => {
    if (this.compactCards()) return true;
    const railOpen = this.orchSideSheetSig()?.open() ?? false;
    if (!railOpen) return false;
    return !this.uiPrefs.userOverridesCompactWhileRail();
  });

  /**
   * F43: tooltip text for the toolbar compact toggle. Derived from
   * `effectiveCompactCards` (not the persisted pref) so the hover text
   * always matches what the user actually sees on the cards: when the
   * rail forces compact, the tooltip still says "Show full cards"
   * because the next click will actually flip the cards to Full.
   */
  readonly compactToggleTooltip = computed<string>(() =>
    this.effectiveCompactCards() ? 'Show full cards' : 'Show compact cards (titles only)',
  );
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

  onSidesheetClearAll(): void {
    this.boardFilters.clearSearchAndFilters();
  }

  /**
   * F25: opens the activity-bar Filters panel and focuses the search
   * input inside the inline filter UI. Bound to the `/` keyboard
   * shortcut, which previously toggled the right-edge filter sheet
   * before the sheet was collapsed into a single source-of-truth
   * activity-bar panel.
   */
  private openFiltersPanelAndFocusSearch(): void {
    if (this.studioPanelState.active() !== 'filters' || !this.studioPanelState.visible()) {
      this.studioPanelState.toggle('filters');
      if (!this.studioPanelState.visible()) {
        this.studioPanelState.setVisible(true);
      }
    }
    queueMicrotask(() => {
      const input = document.querySelector<HTMLInputElement>(
        '[data-testid="kanban-filter-sidesheet-search"]',
      );
      input?.focus();
      input?.select();
    });
  }

  readonly projectNames = computed(() => {
    return this.watchPaths().map((wp) => wp.name);
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
      const el = document.querySelector(
        '[data-testid="lane-3a-failed-pickup"]',
      ) as HTMLElement | null;
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
  /** Workspace tag registry, refreshed on init via `loadTagRegistry`. */
  readonly tagRegistry = this.tagRegistryStore.tags;
  readonly tagRegistryById = this.tagRegistryStore.byId;

  /** Static option list for the type filter dropdown. */
  readonly typeFilterOptions: readonly TypeFilterOption[] = [
    { value: 'bug', label: 'Bugs', icon: '🐞', kind: 'bug' },
    { value: 'feature', label: 'Features', icon: '✨', kind: 'feature' },
    { value: 'chore', label: 'Chores', icon: '·', kind: 'chore' },
  ];

  setClientFilter(id: string | null): void {
    this.boardFilters.setClientFilter(id);
  }
  clientFilterChange(event: Event): string | null {
    return this.boardFilters.clientFilterChange(event);
  }
  clearTypeFilters(): void {
    this.boardFilters.clearTypeFilters();
  }
  onSetType(type: string | null): void {
    this.boardFilters.onSetType(type);
  }
  toggleTypeFilter(type: string): void {
    this.boardFilters.toggleTypeFilter(type);
  }
  toggleTagFilter(id: string): void {
    this.boardFilters.toggleTagFilter(id);
  }
  loadTagRegistry(): void {
    this.jobService.listTags().subscribe({
      next: (tags) => this.tagRegistryStore.set(tags),
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
      {
        state: '1a-orchestrator-prep',
        title: 'Orch Prep',
        icon: '🤖',
        jobs: grouped.orchestratorPrep,
      },
    ];
    if (grouped.needsHumanReview.length > 0) {
      lanes.push({
        state: '1b-needs-human-review',
        title: 'Needs Clar',
        icon: '🚩',
        jobs: grouped.needsHumanReview,
      });
    }
    lanes.push(
      { state: '2-ready', title: 'Ready', icon: '📦', jobs: grouped.ready },
      { state: '3-progress', title: 'In Progress', icon: '🔵', jobs: grouped.progress },
    );
    // ADR-0028: 3a-failed-pickup is hide-when-empty. The card style and the
    // amber outline live on the column component when state === '3a-failed-pickup'.
    if ((grouped.failedPickup ?? []).length > 0) {
      lanes.push({
        state: '3a-failed-pickup',
        title: 'Failed Pickup',
        icon: '⚠️',
        jobs: grouped.failedPickup,
      });
    }
    lanes.push(
      { state: '4-auto-review', title: 'Auto Review', icon: '🤖', jobs: grouped.autoReview },
      { state: '5-human-review', title: 'Human Review', icon: '👁️', jobs: grouped.humanReview },
      { state: '6-completed', title: 'Completed', icon: '🟢', jobs: grouped.completed },
      { state: '7-archive', title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] },
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
    //
    // Order inside the Backlog super-column (top → bottom): the most
    // actionable lanes come first so the user reading the column from the
    // top reaches "what should I pick up next?" without scrolling. Earlier
    // ordering (0-backlog → 2-ready) buried the Ready lane under hundreds
    // of backlog items.
    //
    //   1. 2-ready      "Human Ready"        — pick-up candidates
    //   2. 1b-needs-human-review (if any)    — needs clarification
    //   3. 1a-orchestrator-prep              — agent is preparing
    //   4. 1-preparation                     — in human preparation
    //   5. 0-backlog                         — fresh inbox / triage
    const readySplit = splitReadyByPhase(grouped.ready);
    const backlogLanes: { state: string; title: string; icon: string; jobs: JobInfo[] }[] = [];
    backlogLanes.push({
      state: '2-ready',
      title: 'Human Ready',
      icon: '📦',
      jobs: readySplit.humanReady,
    });
    if (readySplit.intake.length > 0) {
      backlogLanes.push({
        state: '2-ready-intake',
        title: 'Orch Intake',
        icon: '🛂',
        jobs: readySplit.intake,
      });
    }
    if (grouped.needsHumanReview.length > 0) {
      backlogLanes.push({
        state: '1b-needs-human-review',
        title: 'Needs Clar',
        icon: '🚩',
        jobs: grouped.needsHumanReview,
      });
    }
    backlogLanes.push({
      state: '1a-orchestrator-prep',
      title: 'Orch Prep',
      icon: '🤖',
      jobs: grouped.orchestratorPrep,
    });
    backlogLanes.push({
      state: '1-preparation',
      title: 'In Preparation',
      icon: '📋',
      jobs: grouped.preparation,
    });
    backlogLanes.push({
      state: '0-backlog',
      title: 'Backlog',
      icon: '🗒️',
      jobs: grouped.backlog ?? [],
    });
    const activeLanes: { state: string; title: string; icon: string; jobs: JobInfo[] }[] = [
      { state: '3-progress', title: 'In Progress', icon: '🔵', jobs: grouped.progress },
    ];
    // ADR-0028: 3a-failed-pickup is hide-when-empty.
    if ((grouped.failedPickup ?? []).length > 0) {
      activeLanes.push({
        state: '3a-failed-pickup',
        title: 'Failed Pickup',
        icon: '⚠️',
        jobs: grouped.failedPickup,
      });
    }
    activeLanes.push({
      state: '4-auto-review',
      title: 'Auto Review',
      icon: '🤖',
      jobs: grouped.autoReview,
    });
    return [
      {
        id: 'backlog',
        label: 'Backlog',
        lanes: backlogLanes,
      },
      {
        id: 'active',
        label: 'Active',
        lanes: activeLanes,
      },
      {
        id: 'decide',
        label: 'Done & Decide',
        lanes: [
          // ADR-0025: human-review waits on the user; it sits alongside
          // completed and archive in the user-owned tail.
          { state: '5-human-review', title: 'Human Review', icon: '👁️', jobs: grouped.humanReview },
          { state: '6-completed', title: 'Completed', icon: '🟢', jobs: grouped.completed },
          { state: '7-archive', title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] },
        ],
      },
    ];
  });
  readonly selectedJobUsesCopilot = computed(
    () => (this.selectedJob()?.info.cliType ?? 'copilot') === 'copilot',
  );

  // Cycle 9j: triageLanePeers lives in JobSelectionService.
  readonly triageLanePeers = this.jobSelection.triageLanePeers;

  /** Position (1-based) + total used by the slim-tab pager next to the
   *  prev/next arrows. The user pointed out the pager had no number — now
   *  it reads "2 / 26" when the user is on the second card in a 26-card
   *  lane. Falls back to "0 / 0" when there's no selected job. */
  readonly slimPagerPosition = computed<number>(() => {
    const job = this.selectedJob();
    const peers = this.triageLanePeers();
    if (!job || peers.length === 0) return 0;
    const idx = peers.findIndex((p) => p.jobKey === job.info.jobKey);
    return idx >= 0 ? idx + 1 : 0;
  });
  readonly slimPagerTotal = computed<number>(() => this.triageLanePeers().length);

  // Cycle 10a: form state (newTitle/newPrompt/newCliType/etc.) lives in
  // CreateJobFormService. Pass-through getters keep the existing
  // template `[(ngModel)]` and helper-method bindings working unchanged.
  readonly cliTypes = CLI_TYPES;

  cliTypeLabel(t: CliType): string {
    return fmtCliTypeLabel(t);
  }
  formatMultiplier(mult: number | null): string {
    return fmtMultiplier(mult);
  }
  onCreateCliTypeChange(t: CliType): void {
    this.createJobForm.onCreateCliTypeChange(t);
  }
  onDefaultCliChange(t: CliType): void {
    void t;
    this.createJobForm.applyStoredCliDefault();
  }
  onDefaultModelChange(ev: { cliType: CliType; model: string }): void {
    this.createJobForm.onDefaultModelChange(ev);
  }
  canAddTaskToGroup(state: string): boolean {
    return this.createJobForm.canAddTaskToGroup(state);
  }

  readonly devToolsFlags = computed(() => this.devTools.flags());

  /**
   * F23: typed menu-item list driving the shared <app-menu> in the header.
   * Replaces the inline button-per-row markup that lived directly in
   * app.html (and its companion .devtools-menu* SCSS block).
   */
  readonly devtoolsMenuItems = computed<readonly MenuItem[]>(() => {
    const flags = this.devToolsFlags();
    const items: MenuItem[] = [
      { kind: 'header', label: 'System' },
      {
        kind: 'row',
        id: 'orch-config',
        label: 'Orchestrator config',
        hint: 'supervisor + meta-cycle flags',
      },
    ];
    if (flags.updateStableEnabled || flags.deleteE2EJobsEnabled) {
      items.push({ kind: 'header', label: 'Dev tools' });
    }
    if (flags.updateStableEnabled) {
      items.push({
        kind: 'row',
        id: 'update-stable',
        label: 'Update Stable',
        hint: 'open resilient update center',
      });
    }
    if (flags.deleteE2EJobsEnabled) {
      items.push({
        kind: 'row',
        id: 'delete-e2e',
        label: 'Delete E2E Tasks',
        hint: 'across all projects',
        danger: true,
      });
    }
    return items;
  });

  onDevtoolsMenuItemClick(ev: MenuItemClickEvent): void {
    switch (ev.id) {
      case 'orch-config':
        this.onPickOrchestratorConfig();
        break;
      case 'update-stable':
        this.onPickUpdateStable();
        break;
      case 'delete-e2e':
        this.onPickDeleteE2E();
        break;
    }
  }

  onPickUpdateStable(): void {
    this.devToolsMenuOpen.set(false);
    this.updateClient.openCenter();
    void this.updateClient.refreshNow();
  }

  /**
   * Slim detail-header proxies — the studio tab-bar surfaces a few
   * task-tab actions that live on the embedded JobDetailComponent.
   * Forward the click through the ViewChild so the action runs on the
   * same component instance the user is looking at.
   */
  onShellTogglePane(pane: 'prompt' | 'protocol' | 'git'): void {
    this.jobDetailRef?.togglePane(pane);
  }

  onPickDeleteE2E(): void {
    this.devToolsMenuOpen.set(false);
    this.showE2ECleanup.set(true);
  }

  onPickOrchestratorConfig(): void {
    this.devToolsMenuOpen.set(false);
    this.openOrchestratorSettings();
  }

  openOrchestratorSettings(): void {
    this.orchestratorSettingsOpen.set(true);
  }

  closeOrchestratorSettings(): void {
    this.orchestratorSettingsOpen.set(false);
  }

  /**
   * F2: id of a job that was just created via the +Add dialog. Lane
   * cards binding this signal render a brief highlight pulse + scroll
   * themselves into view so a new task isn't lost on a 200+ card board.
   * Cleared automatically after one animation cycle.
   */
  readonly justCreatedJobId = signal<string | null>(null);

  constructor() {
    // Cycle 10a: refresh the kanban after a successful create — the
    // CreateJobFormService doesn't call jobService.refresh itself
    // because that orchestration concern lives here. F2: also flag the
    // new card so it pulses + scrolls into view, and surface a toast
    // with the title that the operator just submitted.
    this.createJobForm.submitted$.subscribe(({ jobId }) => {
      this.refresh();
      this.justCreatedJobId.set(jobId);
      const job = this.jobService.jobs().find((j) => j.id === jobId);
      const title = job?.title ?? jobId;
      this.notifications.success(`Created "${title}"`, 'Task added');
      setTimeout(() => {
        if (this.justCreatedJobId() === jobId) this.justCreatedJobId.set(null);
      }, 2500);
    });

    // Cycle 10c: bridge TriageController to the JobDetailComponent's
    // "acting" highlight via a closure so the ViewChild can resolve
    // lazily at call time. The closure is fine to register here because
    // it doesn't dereference jobDetailRef until invoked.
    this.triage.setClearActingCallback(() => this.jobDetailRef?.clearTriageActing());

    // Studio-shell mirror: when a board tab is opened in the studio shell,
    // sync the BoardFiltersService.activeProjects so the projected
    // dashboard actually narrows to that project. Without this the
    // titlebar pill (and explorer click) felt cosmetic — the lanes still
    // showed all projects' jobs.
    effect(() => {
      if (!this.featureFlags.vsCodeLayout()) return;
      const tab = this.studioTabState.activeTab();
      if (!tab) return;
      untracked(() => {
        if (tab.kind === 'board') {
          if (tab.projectName === '__all__') {
            // Clear project filter for the "All projects" pill.
            this.boardFilters.activeProjects.set(new Set<string>());
            try {
              localStorage.setItem('activeProjects', JSON.stringify([]));
            } catch {
              /* storage may be blocked */
            }
          } else {
            // Idempotent set: only this project's jobs render in the lanes.
            // Uses setSoleProject (not selectProject) so repeated tab
            // activations don't toggle the filter off.
            this.boardFilters.setSoleProject(tab.projectName);
          }
        }
      });
    });

    // Studio-shell mirror: when a job is selected through any path (URL
    // restore, board click, triage advance) and the new shell is on,
    // mirror it as a studio task tab so the editor area can project
    // <app-job-detail> via the task case. Without this the URL-restore
    // path would set selectedJob() but the new shell would show no tab.
    effect(() => {
      const selected = this.selectedJob();
      if (!this.featureFlags.vsCodeLayout()) return;
      if (!selected) return;
      const key = `task:${selected.info.jobKey}`;
      const tabs = untracked(() => this.studioTabState.tabs());
      const present = tabs.some((t) => t.kind === 'task' && t.jobKey === selected.info.jobKey);
      untracked(() => {
        if (!present) {
          this.studioTabState.open({ kind: 'task', jobKey: selected.info.jobKey });
        } else {
          this.studioTabState.select(key);
        }
      });
    });

    effect(() => {
      const selected = this.selectedJob();
      const jobs = this.jobService.jobs();

      if (!selected) {
        return;
      }

      const latest = jobs.find((job) => job.jobKey === selected.info.jobKey);
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

    // F43: clear the user's rail-open compact override the moment the
    // rail closes. Without this, opening the rail a second time would
    // skip the auto-compact rule because the override flag from the
    // previous rail-open session was still latched. The override is
    // intentionally per-tab and ephemeral.
    effect(() => {
      const ref = this.orchSideSheetSig();
      const railOpen = ref?.open() ?? false;
      if (railOpen) return;
      untracked(() => {
        if (this.uiPrefs.userOverridesCompactWhileRail()) {
          this.uiPrefs.userOverridesCompactWhileRail.set(false);
        }
      });
    });

    // External lane change: when the open job's state diverges from
    // `triageLaneState` and we did NOT initiate the move (no actingId
    // in flight), keep the user on this job but shrink the pager
    // snapshot so Prev/Next navigate the remaining peers.
    effect(() => {
      const sel = this.selectedJob();
      const lane = this.jobSelection.triageLaneState;
      if (!sel || !lane) return;
      if (sel.info.state === lane) return;
      if (this.jobDetailRef?.triageActingId() != null) return;
      untracked(() =>
        this.triage.handleExternalLaneChange(lane, sel.info.jobKey),
      );
    });

    // F56: failed-pickup count → toast notification instead of inline banner.
    let failedPickupToastId: number | null = null;
    let lastPickupCount = 0;
    effect(() => {
      const count = this.failedPickupCount();
      if (count === lastPickupCount) return;
      lastPickupCount = count;

      untracked(() => {
        if (failedPickupToastId !== null) {
          this.notifications.dismiss(failedPickupToastId);
          failedPickupToastId = null;
        }
        if (count > 0) {
          failedPickupToastId = this.notifications.notify({
            kind: 'warning',
            title: 'Failed pickup',
            message: `${count} ${count === 1 ? 'task' : 'tasks'} failed to pick up.`,
            durationMs: 0,
            actions: [
              {
                label: 'Open failed-pickup lane',
                testId: 'toast-failed-pickup-open-lane',
                primary: true,
                callback: () => {
                  this.scrollToFailedPickupLane();
                  failedPickupToastId = null;
                },
              },
            ],
          });
        }
      });
    });
  }

  ngOnInit() {
    // Backlog-lane spec: hydrate the filter bar from the URL hash before
    // rendering so a bookmark or copy-paste lands on the same view.
    this.boardFilters.hydrateFromUrl();
    this.loadTagRegistry();
    // ADR-0046: pre-fetch every CLI's model catalog at boot so the
    // chat-model badge, status-bar picker, and create dialog can render
    // their model lists synchronously instead of paying a round-trip on
    // first open.
    this.cliCatalogStore.hydrateAll();
    // 1-Hz wall-clock tick for the lane status RUNNING pill's elapsed
    // string. Light enough to leave running without gating - the only
    // consumer is the lane column's statusCluster computed.
    this.nowMsTickHandle = setInterval(() => this.nowMs.set(Date.now()), 1000);
    this.refresh();
    this.jobService.startLiveUpdates();
    this.jobService.getWatchPaths().subscribe({
      next: (entries) => {
        this.watchPaths.set(entries);
        if (entries.length > 0) this.createJobForm.newWatchPath = entries[0].path;

        // Purge stale project names that survived a registry rename in
        // localStorage (board filter) and persisted tabs.
        const validNames = new Set(entries.map(e => e.name));
        this.boardFilters.purgeStaleProjects(validNames);
        this.studioTabState.purgeStaleProjectTabs(validNames);

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
          source: 'Project list',
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
      const ids = this.laneGroups().map((g) => g.id);
      if (ev.key === '1' && ids[0]) {
        this.toggleContainerFocus(ids[0]);
        ev.preventDefault();
        return;
      }
      if (ev.key === '2' && ids[1]) {
        this.toggleContainerFocus(ids[1]);
        ev.preventDefault();
        return;
      }
      if (ev.key === '3' && ids[2]) {
        this.toggleContainerFocus(ids[2]);
        ev.preventDefault();
        return;
      }
      if (ev.key === '0') {
        this.clearContainerFocus();
        ev.preventDefault();
        return;
      }
      // F25: `/` opens the activity-bar Filters panel and focuses its
      // search input. Replaces the previous binding that toggled the
      // right-edge filter sidesheet.
      if (ev.key === '/' && this.featureFlags.vsCodeLayout()) {
        this.openFiltersPanelAndFocusSearch();
        ev.preventDefault();
        return;
      }
    };
    window.addEventListener('keydown', this.kanbanKeyListener);

    // Global Ctrl+B (Cmd+B on macOS): focus the sticky default board tab.
    // Sister to the activity-bar Board button — the user can be inside any
    // task / hub / diff tab and snap back to the board without aiming for
    // a button. Suppressed inside text inputs so Ctrl+B keeps its bold
    // behaviour in markdown editors / textareas.
    this.boardShortcutListener = (ev: KeyboardEvent) => {
      if (ev.defaultPrevented) return;
      if (!this.featureFlags.vsCodeLayout()) return;
      const mod = ev.ctrlKey || ev.metaKey;
      if (!mod || ev.altKey || ev.shiftKey) return;
      if (ev.key.toLowerCase() !== 'b') return;
      const target = ev.target as HTMLElement | null;
      if (target) {
        const tag = target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
        if (target.isContentEditable) return;
      }
      this.studioTabState.activateSticky();
      ev.preventDefault();
    };
    window.addEventListener('keydown', this.boardShortcutListener);
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
    if (this.boardShortcutListener) {
      window.removeEventListener('keydown', this.boardShortcutListener);
      this.boardShortcutListener = null;
    }
    if (this.nowMsTickHandle !== null) {
      clearInterval(this.nowMsTickHandle);
      this.nowMsTickHandle = null;
    }
  }

  onE2EDidDelete(): void {
    this.jobService.refresh(true);
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: JobInfo) {
    this.jobSelection.openDetail(job);
  }
  closeDetail() {
    this.jobSelection.closeDetail();
  }
  isSelectedJob(job: JobInfo): boolean {
    return this.jobSelection.isSelected(job);
  }

  // Cycle 10c: triage panel + j/k navigation + auto-advance delegated
  // --- studio slim tab-bar triage cluster ---------------------------------
  // Mirror of the kanban detail-header's primary + overflow cluster, anchored
  // in the studio shell's slim tab-bar header (the VS Code layout hides
  // <app-detail-header>, so the cluster needs its own seat there). All
  // actions route through the same TriageController paths as the kanban
  // panel; this is purely a second render site.
  readonly studioTriagePrimary = computed<TriageButton | null>(() => {
    const sel = this.selectedJob();
    return sel ? primaryActionFor(sel.info.state) : null;
  });
  readonly studioTriageOverflow = computed<TriageButton[]>(() => {
    const sel = this.selectedJob();
    return sel ? overflowActionsFor(sel.info.state) : [];
  });
  readonly studioTriageHasActions = computed(
    () => this.studioTriagePrimary() !== null || this.studioTriageOverflow().length > 0,
  );
  readonly studioTriageOverflowOpen = signal(false);
  readonly studioTriageOverflowAnchor = signal<HTMLElement | null>(null);
  readonly studioTriageMenuItems = computed<MenuItem[]>(() => {
    const blocked = this.updateClient.mutationsBlocked();
    return this.studioTriageOverflow().map<MenuItem>(b => ({
      kind: 'row',
      id: b.id,
      label: b.label,
      danger: b.variant === 'danger',
      disabled: blocked,
    }));
  });

  onStudioTriagePrimary(): void {
    const sel = this.selectedJob();
    const p = this.studioTriagePrimary();
    if (!sel || !p) return;
    this.dispatchStudioTriage(sel.info, p);
  }

  toggleStudioTriageOverflow(event: MouseEvent): void {
    event.stopPropagation();
    if (this.updateClient.mutationsBlocked()) return;
    this.studioTriageOverflowAnchor.set(event.currentTarget as HTMLElement);
    this.studioTriageOverflowOpen.update(v => !v);
  }

  closeStudioTriageOverflow(): void {
    this.studioTriageOverflowOpen.set(false);
  }

  onStudioTriageMenuItemClick(ev: MenuItemClickEvent): void {
    const button = this.studioTriageOverflow().find(b => b.id === ev.id);
    const sel = this.selectedJob();
    if (!sel || !button) return;
    this.studioTriageOverflowOpen.set(false);
    if (button.id === 'delete') {
      this.onDeleteFromDetail(sel.info);
      return;
    }
    this.dispatchStudioTriage(sel.info, button);
  }

  private dispatchStudioTriage(info: JobInfo, button: TriageButton): void {
    const id = button.id;
    switch (button.intent.kind) {
      case 'move':
        this.onTriageMove(info, { targetState: button.intent.targetState, actionId: id });
        return;
      case 'moveToTop':
        this.onTriageMoveToTop(info, { actionId: id });
        return;
      case 'delete':
        this.onTriageDelete(info, { actionId: id });
        return;
      case 'start':
        this.onTriageStart(info, { actionId: id });
        return;
      case 'stop':
      case 'editPrompt':
      case 'showActivity':
        // Pane-local intents (stop a running job, jump to the prompt editor,
        // switch the inspector to activity) only make sense inside the
        // detail panel. The kanban detail-header still surfaces them; the
        // studio slim header skips them to keep the row short.
        return;
    }
  }

  // to TriageController. The shell forwards events from JobDetailComponent.
  onTriageMove(info: JobInfo, ev: { targetState: string; actionId: string }) {
    this.triage.move(info, ev);
  }
  onTriageMoveToTop(info: JobInfo, ev: { actionId: string }) {
    this.triage.moveToTop(info, ev);
  }
  onTriageDelete(info: JobInfo, ev: { actionId: string }) {
    this.triage.delete(info, ev);
  }
  onTriageStart(info: JobInfo, ev: { actionId: string }) {
    this.triage.start(info, ev);
  }
  onTriageNext(info: JobInfo) {
    this.triage.next(info);
  }
  onTriagePrev(info: JobInfo) {
    this.triage.prev(info);
  }
  onCompleteAndNextReview() {
    this.triage.completeAndNextReview();
  }

  // Cycle 10b: board-mutation handlers delegate to BoardMutationsService.
  onJobDrop(event: { jobId: string; watchPath: string; targetState: string; targetIndex: number }) {
    this.boardMutations.moveJob(event);
  }
  onJobReorder(event: { state: string; jobs: { jobId: string; watchPath: string }[] }) {
    this.boardMutations.reorderJobs(event);
  }
  onDeleteFromBoard(job: JobInfo) {
    this.boardMutations.deleteFromBoard(job);
  }

  /**
   * F5: bubble-up from <app-job-card>'s "Pick next" affordance.
   * Promotes the card to position 1 in the runner queue. Re-uses the
   * existing `moveJobToTop` round-trip — same endpoint the detail-view
   * "Do Next" already drove. Toasts on success so the operator gets a
   * clear "the runner now sees this first" moment.
   */
  onPickNext(job: JobInfo) {
    this.jobService.moveJobToTop(job.id, job.watchPath).subscribe({
      next: () => {
        this.notifications.success(
          `"${job.title || job.id}" is next up`,
          'Pick next',
        );
        this.refresh();
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to bump task',
          fallbackMessage: 'Failed to bump task to the front of the queue',
          source: `Task ${job.id}`,
        });
      },
    });
  }
  onDeleteFromDetail(info: JobInfo) {
    this.boardMutations.deleteFromDetail(info);
  }
  onStateChangeFromDetail(info: JobInfo, targetState: string) {
    this.boardMutations.changeStateFromDetail(info, targetState);
  }
  onArchiveAll() {
    this.boardMutations.archiveAllCompleted(this.filteredGrouped().completed);
  }

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
   * Toggle the orchestrator feed overlay. Picks the project to show by
   * preferring (1) the currently open detail's project, (2) the first
   * active project filter, (3) the first known watch path. Closes the
   * overlay if it is already open.
   */
  toggleOrchFeed(): void {
    if (this.orchFeedProject() !== null) {
      this.projectOverlays.closeOrchFeed();
      return;
    }
    const project = this.pickOrchFeedProject();
    if (!project) return;
    this.projectOverlays.openOrchFeed(project);
  }

  closeOrchFeed(): void {
    this.orchFeedProject.set(null);
  }

  /** Tooltip for the toolbar button; shows which project the feed will open for. */
  orchFeedTooltip(): string {
    const project = this.pickOrchFeedProject();
    return project ? `Open orchestrator feed for "${project}"` : 'No project selected';
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
    return project ? `Toggle orchestrator chat for "${project}"` : 'No project selected';
  }

  toggleOrchestratorChat(): void {
    this.orchSideSheetRef?.toggle();
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
  onOpenVerboseDebugFromSheet(event: {
    jobId: string;
    watchPath: string;
    jobTitle: string | null;
  }): void {
    forkJoin({
      detail: this.jobService.getDetail(event.jobId, event.watchPath),
      lines: this.jobService.getJobOutput(event.jobId, event.watchPath),
      runs: this.jobService.getRunTimeline(event.jobId, event.watchPath),
      screenshots: this.jobService.getJobScreenshots(event.jobId, event.watchPath),
    }).subscribe({
      next: ({ detail, lines, runs, screenshots }) => {
        this.verboseDebugContext.set({
          lines: lines ?? [],
          runTimeline: runs ?? null,
          screenshots: screenshots?.screenshots ?? [],
          tokenSummary: detail?.info?.tokenSummary ?? null,
          job: detail?.info ?? null,
        });
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Verbose Debug failed to load',
          source: `task ${event.jobTitle ?? event.jobId}`,
        });
      },
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
      `?job=${encodeURIComponent(event.jobId)}&watchPath=${encodeURIComponent(event.watchPath)}`,
    );
    const token = this.jobSelection.bumpOpenDetailToken();
    this.jobService.getDetail(event.jobId, event.watchPath).subscribe({
      next: (detail) => this.jobSelection.setSelectedFromAdvance(detail, token),
      error: (err) => {
        history.replaceState(null, '', window.location.pathname);
        this.errorDialog.show(err, {
          title: 'Failed to open task',
          source: `task ${event.jobId}`,
        });
      },
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
  onSecurityOpenEvidence(event: { projectName: string; relPath: string }): void {
    void event;
    // The relPath is rendered in the panel row itself; refresh the kanban
    // so a freshly-queued audit's eventual completion is visible without
    // a manual reload.
    this.refresh();
  }

  /** Refresh the kanban after a security audit was queued so the new job appears. */
  onSecurityAuditQueued(event: { projectName: string; jobId: string }): void {
    void event;
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
  onUxuiActionQueued(event: { projectName: string; action: string; jobId: string }): void {
    void event;
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
  closeProjectShell(): void {
    this.projectOverlays.closeProjectShell();
  }
  openAnalysisReport(project: string, reportId: string): void {
    this.projectOverlays.openAnalysisReport(project, reportId);
  }
  closeAnalysisReport(): void {
    this.projectOverlays.closeAnalysisReport();
  }
  private applyProjectShellHash(): void {
    this.projectOverlays.syncShellFromHash(this.watchPaths());
  }

  // Cycle 9g: workspace overlay open/close + URL-hash sync delegated to
  // WorkspaceOverlaysService. The shell keeps these thin pass-throughs
  // because external call sites (status bar, usage hover panel, dev-tools
  // menu, screenshot reel) and deep-link entry points still go through
  // the shell.
  openWorkspaceTokens(): void {
    this.workspaceOverlays.openTokens();
  }
  closeWorkspaceTokens(): void {
    this.workspaceOverlays.closeTokens();
  }
  openWorkspaceScreenshots(): void {
    this.workspaceOverlays.openScreenshots();
  }
  closeWorkspaceScreenshots(): void {
    this.workspaceOverlays.closeScreenshots();
  }
  toggleWorkspaceScreenshots(): void {
    this.workspaceOverlays.toggleScreenshots();
  }
  openCliAdmin(): void {
    this.workspaceOverlays.openCliAdmin();
  }
  closeCliAdmin(): void {
    this.workspaceOverlays.closeCliAdmin();
  }
  toggleCliAdmin(): void {
    this.workspaceOverlays.toggleCliAdmin();
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
    history.replaceState(
      null,
      '',
      `?job=${encodeURIComponent(s.jobId)}&watchPath=${encodeURIComponent(s.watchPath)}`,
    );
    this.jobService.getDetail(s.jobId, s.watchPath).subscribe({
      next: (detail) => this.selectedJob.set(detail),
      error: () => {
        /* keep the user where they were */
      },
    });
  }

  cancelCreate() {
    this.createJobForm.cancel();
  }
  submitCreate() {
    this.createJobForm.submit();
  }

  toggleProject(event: { name: string; additive: boolean } | string) {
    if (typeof event === 'string') {
      // Legacy call sites (programmatic invocations) keep their toggle
      // semantics so multi-select remains reachable without a modifier key.
      this.boardFilters.toggleProject(event);
      return;
    }
    this.boardFilters.selectProject(event.name, event.additive);
  }
  isProjectActive(name: string): boolean {
    return this.boardFilters.isProjectActive(name);
  }

  // Pre-bound arrow-function aliases for child components that take a
  // predicate-style input (e.g. <app-project-tabs>). Using arrows keeps
  // `this` correct without per-call .bind().
  readonly isProjectActiveFn = (name: string) => this.isProjectActive(name);
  readonly getRunnerIndicatorFn = (name: string) => this.getRunnerIndicator(name);
  readonly getAutoInfoFn = (name: string) => this.getAutoInfo(name);
  readonly getProjectTokenChipFn = (name: string) => this.getProjectTokenChip(name);
  readonly identityFor = (name: string) => projectIdentity(name);

  private getProjectTokenChip(name: string) {
    return buildProjectTokenChip(this.jobService.jobs(), name);
  }

  getRunnerIndicator(name: string): { icon: string; cls: string } | null {
    return projectRunnerIndicator(this.jobService.runnerStatus(), name);
  }

  getAutoInfo(name: string) {
    return projectAutoInfo(this.jobService.runnerStatus(), name);
  }

  onToggleAuto(name: string) {
    const runner = this.jobService.runnerStatus().projects[name];
    const mode = runner?.mode ?? 'manual';
    const newMode =
      mode === 'auto-continuous' || mode === 'auto-single' ? 'paused' : 'auto-continuous';
    this.jobService.setRunnerMode(name, newMode).subscribe({
      next: () => {
        this.jobService.refreshRunnerStatus(true);
        // F6: a short toast on auto-pickup mode flips. The chip itself only
        // changes a small dot/label — easy to miss when the click lands by
        // accident. The toast gives the operator a clear "this just changed"
        // moment.
        const verb = newMode === 'paused' ? 'paused' : 'enabled';
        this.notifications.info(`${name} · auto-pickup ${verb}`, 'Runner mode');
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to change auto-pickup mode',
          fallbackMessage: 'Failed to change auto-pickup mode',
          source: `Project ${name}`,
        });
      },
    });
  }

  /**
   * Project name to attribute the lane-side auto chip to. The chip is
   * meaningful only when there's a single project in scope: either the
   * board is scoped to one project, or every job in the lane belongs to
   * the same project. Otherwise return null so the chip stays hidden.
   */
  laneAutoProject(state: string, jobs: JobInfo[]): string | null {
    if (state !== '3-progress') return null;
    // Prefer the board's scoped project (Picker selection); fall back to
    // the only project actually in the lane.
    const tab = this.studioTabState.activeTab();
    if (tab?.kind === 'board' && tab.projectName !== '__all__') {
      return tab.projectName;
    }
    if (jobs.length === 0) return null;
    const first = jobs[0].projectName ?? null;
    if (!first) return null;
    return jobs.every((j) => j.projectName === first) ? first : null;
  }

  /** Current runner mode (lookup mirrors the studio-shell header chip). */
  laneAutoMode(state: string, jobs: JobInfo[]): string {
    const proj = this.laneAutoProject(state, jobs);
    if (!proj) return 'manual';
    return this.jobService.runnerStatus().projects[proj]?.mode ?? 'manual';
  }

  /**
   * Full runner-status snapshot for the lane's auto project (or null when
   * the lane is not project-scoped). Drives the In-Progress lane's status
   * cluster pills (RUNNING / mode / Q:N).
   */
  laneAutoRunner(state: string, jobs: JobInfo[]) {
    const proj = this.laneAutoProject(state, jobs);
    if (!proj) return null;
    return this.jobService.runnerStatus().projects[proj] ?? null;
  }

  /**
   * 1-Hz wall-clock tick so the RUNNING pill's elapsed-time string
   * (`3m24s`) advances without re-polling /api/runner/status. The lane
   * column reads this from its `nowMs` input; only the lane that renders
   * the RUNNING pill consumes it.
   */
  readonly nowMs = signal(Date.now());
  private nowMsTickHandle: ReturnType<typeof setInterval> | null = null;

  onFileSaved() {
    this.boardMutations.refreshAfterFileSave();
  }
  onProjectChanged(targetWatchPath: string) {
    this.boardMutations.reopenAfterProjectChange(targetWatchPath);
  }

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

  toggleLaneCollapse(state: string): void {
    this.laneCollapse.toggleLaneCollapse(state);
  }
  isLaneCollapsed(state: string): boolean {
    return this.laneCollapse.isLaneCollapsed(state);
  }
  expandedLaneCount(group: { lanes: { state: string }[] }): number {
    return this.laneCollapse.expandedLaneCount(group);
  }
  isContainerFocused(id: string): boolean {
    return this.laneCollapse.isContainerFocused(id);
  }
  toggleContainerFocus(id: string): void {
    this.laneCollapse.toggleContainerFocus(
      id,
      this.laneGroups().map((g) => g.id),
    );
  }
  clearContainerFocus(): void {
    this.laneCollapse.clearContainerFocus();
  }

  // Cycle 9: UI-pref methods delegate to UiPreferencesService.
  setTaskNavCollapsed(collapsed: boolean): void {
    this.uiPrefs.setTaskNavCollapsed(collapsed);
  }
  /**
   * F43: toggle the card-density preference, honouring the user's
   * intent even when the orchestrator rail is auto-forcing compact.
   *
   * Pre-F43 the toolbar toggle just flipped the persisted pref. With
   * the rail open the pref no longer drove the effective density, so
   * "Use full cards" felt broken: pref flipped to false, cards stayed
   * compact, only a toast hinted that the click had landed somewhere.
   *
   * Now: write the pref to the value the user *intends* to see
   * (`nextEffective`), and register a per-tab override that overrides
   * the rail-forced compact rule for the rest of this rail-open
   * session. The override is cleared by the effect below when the
   * rail closes so re-opening it re-engages the auto-compact rule.
   */
  toggleCompactCards(): void {
    const railOpen = this.orchSideSheetSig()?.open() ?? false;
    const nextEffective = !this.effectiveCompactCards();
    this.uiPrefs.setCompactCards(nextEffective);
    if (railOpen && !nextEffective) {
      this.uiPrefs.userOverridesCompactWhileRail.set(true);
    } else if (!railOpen) {
      this.uiPrefs.userOverridesCompactWhileRail.set(false);
    }
  }
  startResize(event: MouseEvent): void {
    this.uiPrefs.startResize(event);
  }
}
