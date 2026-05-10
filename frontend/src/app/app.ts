import { ChangeDetectionStrategy, Component, computed, effect, inject, OnInit, signal, untracked, ViewChild, ViewEncapsulation } from '@angular/core';
import { LaneCollapseService } from './features/board/state/lane-collapse.service';
import { UiPreferencesService } from './features/shell/state/ui-preferences.service';
import { BoardFiltersService, ActiveFilterPill } from './features/board/state/board-filters.service';
import { forkJoin } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { JobColumnComponent } from './features/board/components/job-column';
import { JobDetailComponent } from './features/job-detail/job-detail';
import { CliUsageSheetComponent } from './features/cli/components/cli-usage-sheet';
import { OrchestratorFeedComponent } from './features/orchestrator/components/orchestrator-feed';
import { OrchestratorSideSheetComponent } from './features/orchestrator/components/orchestrator-side-sheet/orchestrator-side-sheet.component';
import { ProjectDetailComponent } from './features/project-detail/components/project-detail';
import { ProjectShellComponent } from './features/project-detail/components/project-shell/project-shell.component';
import { SecurityPanelComponent } from './features/project-detail/components/security-panel/security-panel.component';
import { UxuiPanelComponent } from './features/project-detail/components/uxui-panel/uxui-panel.component';
import { ProjectTokenUsagePanelComponent } from './features/project-token-usage/components/project-token-usage-panel.component';
import { ProjectObservabilityPanelComponent } from './features/project-detail/components/project-observability/project-observability-panel.component';
import { ProjectProductRuntimePanelComponent } from './features/project-detail/components/project-product-runtime/project-product-runtime-panel.component';
import { ProjectSteeringDocsSectionComponent } from './features/project-detail/components/project-steering-docs-section';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectRailKey,
  ProjectRailKey,
  toProjectSlug,
} from './features/project-detail/components/project-shell/project-shell.config';
import { AnalysisReportDrilldownComponent } from './features/project-detail/components/analysis-report-drilldown';
import { StatusBarComponent } from './components/status-bar';
import { JobService } from './services/job.service';
import { ClientService } from './services/client.service';
import { JobDetail, JobInfo, WatchPathEntry, CliType, CLI_TYPES, CliModelInfo } from './models/job.model';
import { ErrorDialogService } from './services/error-dialog.service';
import { cliTypeLabel as fmtCliTypeLabel, formatMultiplier as fmtMultiplier } from './services/format.util';
import { CreateJobDialogComponent, PendingAttachment } from './features/board/components/create-job-dialog/create-job-dialog.component';
import { ErrorDialogComponent } from './components/error-dialog/error-dialog.component';
import { ProjectAutoInfo, ProjectTabsComponent, ProjectTokenChipInfo } from './features/board/components/project-tabs/project-tabs.component';
import { FiltersDropdownComponent, TypeFilterOption } from './features/board/components/filters-dropdown/filters-dropdown.component';
import { KanbanFilterSidesheetComponent } from './features/board/components/kanban-filter-sidesheet/kanban-filter-sidesheet.component';
import { UpdateClientService } from './services/update.service';
import { projectIdentity } from './services/project-identity.util';
import { DevToolsService } from './services/dev-tools.service';
import { FeatureFlagsService } from './services/feature-flags.service';
import { JobCompletionSoundService } from './services/job-completion-sound.service';
import { TagRegistryStore } from './services/tag-registry.store';
import { UpdateStableConsoleComponent } from './features/dev-tools/components/update-stable-console.component';
import { E2ECleanupDialogComponent } from './features/dev-tools/components/e2e-cleanup-dialog.component';
import { WorkspaceTokenTimelineComponent } from './features/tokens/components/workspace-token-timeline';
import { WorkspaceScreenshotsComponent } from './features/screenshots/components/workspace-screenshots';
import { WorkspaceBannerComponent } from './features/shell/components/workspace-banner';
import { UpdateBannerComponent } from './features/update/components/update-banner/update-banner.component';
import { UpdateVersionBadgeComponent } from './features/update/components/update-version-badge/update-version-badge.component';
import { UpdateCenterComponent } from './features/update/components/update-center/update-center.component';
import { OrchestratorConfigPanelComponent } from './features/orchestrator/components/orchestrator-config-panel/orchestrator-config-panel.component';
import { UpdateBlockModalComponent } from './features/update/components/update-block-modal/update-block-modal.component';
import { CliAdminPanelComponent } from './features/cli/components/cli-admin-panel';
import { JobScreenshot, RunTimeline, JobTokenSummary, CliOutputLine } from './models/job.model'; // verbose-debug overlay context types
import { VerboseDebugOverlayComponent } from './features/verbose-debug/components/verbose-debug-overlay.component';
import { splitReadyByPhase } from './features/board/components/ready-lane-split.util';

interface VerboseDebugContext {
  lines: CliOutputLine[];
  runTimeline: RunTimeline | null;
  screenshots: JobScreenshot[];
  tokenSummary: JobTokenSummary | null;
  job: JobInfo | null;
}

@Component({
  selector: 'app-root',
  imports: [JobColumnComponent, JobDetailComponent, CliUsageSheetComponent, OrchestratorFeedComponent, OrchestratorSideSheetComponent, ProjectDetailComponent, ProjectShellComponent, SecurityPanelComponent, UxuiPanelComponent, ProjectTokenUsagePanelComponent, ProjectObservabilityPanelComponent, ProjectProductRuntimePanelComponent, ProjectSteeringDocsSectionComponent, AnalysisReportDrilldownComponent, StatusBarComponent, FormsModule, CreateJobDialogComponent, ErrorDialogComponent, ProjectTabsComponent, UpdateStableConsoleComponent, E2ECleanupDialogComponent, WorkspaceTokenTimelineComponent, WorkspaceScreenshotsComponent, WorkspaceBannerComponent, UpdateBannerComponent, UpdateVersionBadgeComponent, UpdateCenterComponent, OrchestratorConfigPanelComponent, UpdateBlockModalComponent, VerboseDebugOverlayComponent, CliAdminPanelComponent, FiltersDropdownComponent, KanbanFilterSidesheetComponent],
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
  template: `
    <div class="app"
         [class.app--vscode-layout]="featureFlags.vsCodeLayout()"
         [class.app--vscode-meta-open]="featureFlags.vsCodeLayout() && featureFlags.vsCodeMetaOpen()"
         [class.app--kanban-spec-v1]="featureFlags.kanbanDesignSpecV1()"
         [class.app--task-open]="!!selectedJob()"
         data-testid="app-root">
      <header class="header">
        <div class="header__brand">
          <img class="header__icon" src="icons/icon.svg" alt="Agent Software Studio" width="20" height="20" />
          <h1 class="header__title">
            <span class="header__title-ai">Agent</span><span class="header__title-sep"></span><span class="header__title-name">Software Studio</span>
          </h1>
        </div>
        <app-project-tabs
          [names]="projectNames()"
          [isActive]="isProjectActiveFn"
          [runnerIndicator]="getRunnerIndicatorFn"
          [autoInfo]="getAutoInfoFn"
          [projectTokens]="getProjectTokenChipFn"
          (toggle)="toggleProject($event)"
          (toggleAuto)="onToggleAuto($event)"
          (openDetail)="openProjectDetail($event)"
          (openShell)="openProjectShell($event)" />
        <div class="header__actions">
          <app-update-version-badge />
          <label class="client-filter" data-testid="client-filter">
            <span class="client-filter__label">Owner:</span>
            <select class="client-filter__select"
                    data-testid="client-filter-select"
                    [value]="activeClientFilter() ?? ''"
                    (change)="setClientFilter(clientFilterChange($event))">
              <option value="">All</option>
              @for (c of clientService.clients(); track c.id) {
                <option [value]="c.id">{{ c.emoji || '·' }} {{ c.displayName }}</option>
              }
            </select>
          </label>
          <app-filters-dropdown
            [typeOptions]="typeFilterOptions"
            [activeType]="activeType()"
            [tags]="tagRegistry()"
            [activeTagIds]="activeTagFilter()"
            (setType)="onSetType($event)"
            (toggleTag)="toggleTagFilter($event)" />
          @if (boardSearchVisible() && !orchSideSheet.open()) {
            <button type="button"
                    class="header__filter-trigger"
                    data-testid="kanban-filter-sidesheet-trigger"
                    [class.header__filter-trigger--active]="kanbanFilterSidesheetOpen() || hasActiveFiltersOrSearch()"
                    [attr.aria-pressed]="kanbanFilterSidesheetOpen()"
                    [attr.aria-label]="'Open filter and view panel'"
                    title="Filters &amp; view (press /)"
                    (click)="toggleKanbanFilterSidesheet()">
              <span aria-hidden="true">🔍</span>
              @if (searchQuery().trim().length > 0) {
                <span class="header__filter-trigger__chip"
                      data-testid="kanban-filter-sidesheet-trigger-chip">"{{ searchQuery() }}"</span>
              }
              @if (activeFilterCount() > 0) {
                <span class="header__filter-trigger__count"
                      data-testid="kanban-filter-sidesheet-trigger-count">{{ activeFilterCount() }}</span>
              }
            </button>
          }
          <button class="btn btn--compact-toggle"
                  data-testid="compact-cards-toggle"
                  [class.btn--compact-toggle--active]="compactCards()"
                  [attr.aria-pressed]="compactCards()"
                  [title]="compactCards() ? 'Show full cards' : 'Show compact cards (titles only)'"
                  (click)="toggleCompactCards()">
            <span aria-hidden="true">{{ compactCards() ? '▤' : '▥' }}</span>
            <span class="btn--compact-toggle__label">{{ compactCards() ? 'Compact' : 'Full' }}</span>
          </button>
          <button class="btn btn--create" (click)="openCreate()">
            ＋ Add Task
          </button>
          <div class="devtools-menu">
            <button class="devtools-menu__trigger"
                    data-testid="devtools-menu-trigger"
                    title="Dev tools"
                    [class.devtools-menu__trigger--open]="devToolsMenuOpen()"
                    (click)="devToolsMenuOpen.set(!devToolsMenuOpen()); $event.stopPropagation()">⋮</button>
            @if (devToolsMenuOpen()) {
              <div class="devtools-menu__backdrop" (click)="devToolsMenuOpen.set(false)"></div>
              <div class="devtools-menu__panel" (click)="$event.stopPropagation()">
                <div class="devtools-menu__header">System</div>
                <button class="devtools-menu__item"
                        data-testid="devtool-orch-config"
                        (click)="onPickOrchestratorConfig()">
                  <span class="devtools-menu__icon">⚙</span>
                  <span class="devtools-menu__label">Orchestrator config</span>
                  <span class="devtools-menu__hint">supervisor + meta-cycle flags</span>
                </button>
                @if (devToolsFlags().updateStableEnabled || devToolsFlags().deleteE2EJobsEnabled) {
                  <div class="devtools-menu__header">Dev tools</div>
                }
                @if (devToolsFlags().updateStableEnabled) {
                  <button class="devtools-menu__item"
                          data-testid="devtool-update-stable"
                          (click)="onPickUpdateStable()">
                    <span class="devtools-menu__icon">⟳</span>
                    <span class="devtools-menu__label">Update Stable</span>
                    <span class="devtools-menu__hint">pull main, restart instance</span>
                  </button>
                }
                @if (devToolsFlags().deleteE2EJobsEnabled) {
                  <button class="devtools-menu__item devtools-menu__item--danger"
                          data-testid="devtool-delete-e2e"
                          (click)="onPickDeleteE2E()">
                    <span class="devtools-menu__icon">🧹</span>
                    <span class="devtools-menu__label">Delete E2E Jobs</span>
                    <span class="devtools-menu__hint">across all projects</span>
                  </button>
                }
              </div>
            }
          </div>
        </div>
      </header>

      @if (activeFilterPills().length > 0) {
        <div class="active-filter-strip"
             data-testid="active-filter-strip"
             role="region"
             aria-label="Active filters">
          @for (pill of activeFilterPills(); track pill.kind + ':' + pill.value) {
            <span class="active-filter-strip__pill"
                  [class]="'active-filter-strip__pill--' + pill.kind"
                  [attr.data-testid]="'active-filter-pill-' + pill.kind + '-' + pill.value">
              <span class="active-filter-strip__pill-kind">{{ pill.kindLabel }}:</span>
              @if (pill.swatch) {
                <span class="active-filter-strip__pill-swatch"
                      aria-hidden="true"
                      [style.background]="pill.swatch"></span>
              }
              <span class="active-filter-strip__pill-label">{{ pill.label }}</span>
              <button type="button"
                      class="active-filter-strip__pill-remove"
                      [attr.data-testid]="'active-filter-remove-' + pill.kind + '-' + pill.value"
                      [attr.aria-label]="'Remove ' + pill.kindLabel + ' filter ' + pill.label"
                      (click)="removeFilterPill(pill)">×</button>
            </span>
          }
          <button type="button"
                  class="active-filter-strip__clear-all"
                  data-testid="filter-clear-all"
                  (click)="clearAllFilters()">Clear all</button>
        </div>
      }

      <app-update-banner />
      <app-update-center />
      <app-orchestrator-config-panel #orchConfigPanel />
      <app-update-block-modal />
      <app-workspace-banner [projects]="bannerProjects()" />

      <div class="app__body">
        <div class="layout" [class.layout--focus]="selectedJob()">
        @if (selectedJob(); as detail) {
          <div class="workspace"
               [class.workspace--nav-collapsed]="taskNavCollapsed()"
               [style.--side-sheet-width]="sideSheetWidth() + 'px'">
            @if (taskNavCollapsed()) {
              <aside class="task-nav task-nav--collapsed" data-testid="task-nav-collapsed">
                <button class="task-nav__expand"
                        data-testid="task-nav-expand"
                        title="Expand task list"
                        (click)="setTaskNavCollapsed(false)">›</button>
                <button class="task-nav__expand task-nav__expand--board"
                        data-testid="back-to-board"
                        title="Back to board"
                        (click)="closeDetail()">←</button>
              </aside>
            } @else {
            <aside class="task-nav">
              <div class="task-nav__header">
                <div class="task-nav__header-row">
                  <button class="btn btn--ghost" data-testid="back-to-board" (click)="closeDetail()">← Board</button>
                  <button class="task-nav__collapse"
                          data-testid="task-nav-collapse"
                          title="Collapse task list"
                          (click)="setTaskNavCollapsed(true)">‹</button>
                </div>
                <div>
                  <div class="task-nav__eyebrow">Task list</div>
                  <h2 class="task-nav__title">Focused view</h2>
                </div>
              </div>

              <div class="task-nav__groups">
                @for (group of focusGroups(); track group.state) {
                  <section class="task-nav__group" [class.task-nav__group--collapsed]="isGroupCollapsed(group.state)">
                    <div class="task-nav__group-header" (click)="toggleGroupCollapse(group.state)">
                      <span>
                        <span class="task-nav__group-toggle">{{ isGroupCollapsed(group.state) ? '▶' : '▼' }}</span>
                        {{ group.icon }} {{ group.title }}
                      </span>
                      <span class="task-nav__count">{{ group.jobs.length }}</span>
                    </div>

                    @if (group.jobs.length > 0 && !isGroupCollapsed(group.state)) {
                      <div class="task-nav__items">
                        @for (job of group.jobs; track job.jobKey) {
                          <button class="task-nav__item"
                                  [class.task-nav__item--active]="isSelectedJob(job)"
                                  [style.--project-color]="identityFor(job.projectName).color"
                                  [style.--project-on]="identityFor(job.projectName).onColor"
                                  (click)="openDetail(job)">
                            <span class="task-nav__item-title">{{ job.title || job.id }}</span>
                            <span class="task-nav__item-meta">
                              <span>#{{ job.order }}</span>
                              <span class="task-nav__item-project">
                                <span class="task-nav__item-disk" aria-hidden="true">{{ identityFor(job.projectName).initial }}</span>
                                {{ job.projectName }}
                              </span>
                            </span>
                          </button>
                        }
                      </div>
                    }
                    @if (canAddTaskToGroup(group.state) && !isGroupCollapsed(group.state)) {
                      <button class="task-nav__add" (click)="openCreate(group.state)">
                        <span>＋</span>
                        <span>Add task</span>
                      </button>
                    }
                  </section>
                }
              </div>
              
              <div class="task-nav__resize-handle"
                   (mousedown)="startResize($event)"></div>
            </aside>
            }

            <main class="workspace__main">
              <app-job-detail #jobDetail
                              [detail]="detail"
                              [watchPaths]="watchPaths()"
                              [lanePeers]="triageLanePeers()"
                              [mutationsBlocked]="updateClient.mutationsBlocked()"
                              (back)="closeDetail()"
                              (fileSaved)="onFileSaved()"
                              (projectChanged)="onProjectChanged($event)"
                              (completeAndNextReview)="onCompleteAndNextReview()"
                              (deleteRequested)="onDeleteFromDetail(detail.info)"
                              (stateChangeRequested)="onStateChangeFromDetail(detail.info, $event.targetState)"
                              (triageMoveRequested)="onTriageMove(detail.info, $event)"
                              (triageMoveToTopRequested)="onTriageMoveToTop(detail.info, $event)"
                              (triageDeleteRequested)="onTriageDelete(detail.info, $event)"
                              (triageStartRequested)="onTriageStart(detail.info, $event)"
                              (nextInLaneRequested)="onTriageNext(detail.info)"
                              (prevInLaneRequested)="onTriagePrev(detail.info)" />
              @if (triageToast(); as toast) {
                <div class="triage-toast" data-testid="triage-toast" role="status">{{ toast }}</div>
              }
            </main>
          </div>
        } @else {
          @if (failedPickupCount() > 0) {
            <button type="button"
                    class="failed-pickup-banner"
                    data-testid="failed-pickup-banner"
                    [attr.aria-label]="'Open failed-pickup lane'"
                    (click)="scrollToFailedPickupLane()">
              <span class="failed-pickup-banner__dot" aria-hidden="true"></span>
              <span class="failed-pickup-banner__text">
                <strong data-testid="failed-pickup-banner-count">{{ failedPickupCount() }}</strong>
                {{ failedPickupCount() === 1 ? 'job' : 'jobs' }} failed to pick up.
                Open the failed-pickup lane.
              </span>
              <span class="failed-pickup-banner__chev" aria-hidden="true">›</span>
            </button>
          }
          <main class="dashboard" data-testid="kanban-dashboard"
                [class.dashboard--has-focus]="focusedContainer() !== null">
            @for (g of laneGroups(); track g.id) {
              <section class="lane-group"
                       [attr.data-testid]="'lane-group-' + g.id"
                       [class.lane-group--collapsed]="isContainerCollapsed(g.id)"
                       [class.lane-group--focused]="isContainerFocused(g.id)"
                       [style.flex-grow]="expandedLaneCount(g)">
                <header class="lane-group__head">
                  <button type="button"
                          class="lane-group__toggle"
                          [attr.data-testid]="'lane-group-toggle-' + g.id"
                          [attr.aria-expanded]="!isContainerCollapsed(g.id)"
                          [attr.aria-label]="(isContainerCollapsed(g.id) ? 'Expand ' : 'Collapse ') + g.label"
                          (click)="toggleContainerCollapse(g.id)">
                    {{ isContainerCollapsed(g.id) ? '▶' : '▼' }}
                  </button>
                  <span class="lane-group__label">{{ g.label }}</span>
                  @if (isContainerCollapsed(g.id)) {
                    <span class="lane-group__strip"
                          [attr.data-testid]="'lane-group-strip-' + g.id">
                      @for (chip of containerSummary(g.id); track chip.state) {
                        <span class="lane-group__chip"
                              [attr.data-testid]="'lane-group-chip-' + chip.state"
                              [title]="chip.title">
                          <span class="lane-group__chip-icon" aria-hidden="true">{{ chip.icon }}</span>
                          <span class="lane-group__chip-count">×{{ chip.count }}</span>
                        </span>
                      }
                    </span>
                  }
                  <button type="button"
                          class="lane-group__focus"
                          [attr.data-testid]="'lane-group-focus-' + g.id"
                          [attr.aria-pressed]="isContainerFocused(g.id)"
                          [attr.aria-label]="(isContainerFocused(g.id) ? 'Exit focus on ' : 'Focus ') + g.label"
                          (click)="toggleContainerFocus(g.id)">⤢</button>
                </header>
                @if (!isContainerCollapsed(g.id)) {
                  <div class="lane-group__lanes">
                    @for (lane of g.lanes; track lane.state) {
                      <app-job-column
                        [title]="lane.title"
                        [icon]="lane.icon"
                        [state]="lane.state"
                        [jobs]="lane.jobs"
                        [collapsed]="isLaneCollapsed(lane.state)"
                        [compact]="compactCards()"
                        (collapseToggle)="toggleLaneCollapse(lane.state)"
                        (jobClick)="openDetail($event)"
                        (jobDrop)="onJobDrop($event)"
                        (jobReorder)="onJobReorder($event)"
                        (jobDeleteRequest)="onDeleteFromBoard($event)"
                        (addTask)="openCreate($event)"
                        (archiveAll)="onArchiveAll()" />
                    }
                  </div>
                }
              </section>
            }
          </main>
        }
      </div>

        <app-kanban-filter-sidesheet
          #kanbanFilterSheet
          class="app__sidesheet"
          [open]="kanbanFilterSidesheetOpen()"
          [query]="searchQuery()"
          [typeOptions]="typeFilterOptions"
          [activeType]="activeType()"
          [tags]="tagRegistry()"
          [activeTagIds]="activeTagFilter()"
          [owners]="clientService.clients()"
          [activeOwnerId]="activeClientFilter()"
          [compactCards]="compactCards()"
          [hitCount]="filteredJobCount()"
          [totalCount]="totalJobCount()"
          [hasAnyFilter]="hasActiveFiltersOrSearch()"
          (queryChange)="setSearchQuery($event)"
          (setType)="onSetType($event)"
          (toggleTag)="toggleTagFilter($event)"
          (setOwner)="setClientFilter($event)"
          (toggleCompactCards)="toggleCompactCards()"
          (clearAll)="onSidesheetClearAll()"
          (closed)="closeKanbanFilterSidesheet()" />
        <app-cli-usage-sheet #usageSheet class="app__sidesheet" />
        <app-orchestrator-side-sheet
          #orchSideSheet
          class="app__sidesheet"
          [projects]="projectNames()"
          [preferredProject]="orchSideSheetPreferredProject()"
          [watchPaths]="watchPaths()"
          [activeJobId]="selectedJob()?.info?.id ?? null"
          [activeJobTitle]="selectedJob()?.info?.title ?? null"
          [activeWatchPath]="selectedJob()?.info?.watchPath ?? null"
          (createTaskFromDraft)="onCreateTaskFromOrchestratorDraft($event)"
          (openVerboseDebug)="onOpenVerboseDebugFromSheet($event)"
          (openJobDetail)="onOpenJobDetailFromSheet($event)" />
      </div>

      <app-status-bar
        [projectNames]="projectNames()"
        (toggleUsage)="usageSheet.toggle()"
        (toggleOrchestrator)="orchSideSheet.toggle()"
        (toggleFeed)="toggleOrchFeed()"
        (toggleVisualEvidence)="toggleWorkspaceScreenshots()"
        (toggleCliAdmin)="toggleCliAdmin()"
        (defaultCliChange)="onDefaultCliChange($event)"
        (defaultModelChange)="onDefaultModelChange($event)" />

      @if (orchFeedProject(); as proj) {
        <div class="overlay" (click)="closeOrchFeed()">
          <div class="overlay__panel" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeOrchFeed()" title="Close">×</button>
            <app-orchestrator-feed [projectName]="proj" />
          </div>
        </div>
      }

      @if (projectDetailName(); as proj) {
        <div class="overlay" (click)="closeProjectDetail()">
          <div class="overlay__panel" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeProjectDetail()" title="Close">×</button>
            <app-project-detail
              [projectName]="proj"
              (openFeed)="onOpenFeedFromDetail($event)"
              (openReport)="openAnalysisReport(proj, $event.reportId)" />
          </div>
        </div>
      }

      @if (projectShellName(); as projShell) {
        <div class="overlay overlay--shell" data-testid="project-shell-overlay">
          <div class="overlay__shell-panel">
            <app-project-shell
              [projectName]="projShell"
              [activeRail]="projectShellRail()"
              [hasCustomPanel]="projectShellRail() === 'security' || projectShellRail() === 'uxui' || projectShellRail() === 'token-usage' || projectShellRail() === 'observability' || projectShellRail() === 'product-runtime' || projectShellRail() === 'steering'"
              (railChange)="onProjectShellRailChange($event)"
              (openFeed)="onOpenFeedFromShell()"
              (closeShell)="closeProjectShell()">
              @defer (when projectShellRail() === 'security') {
                @if (projectShellRail() === 'security') {
                  <app-security-panel
                    [projectName]="projShell"
                    (createFollowUp)="onSecurityFollowUp($event)"
                    (openEvidence)="onSecurityOpenEvidence($event)"
                    (auditQueuedEvent)="onSecurityAuditQueued($event)" />
                }
              }
              @defer (when projectShellRail() === 'uxui') {
                @if (projectShellRail() === 'uxui') {
                  <app-uxui-panel
                    [projectName]="projShell"
                    (createFollowUp)="onUxuiFollowUp($event)"
                    (actionQueuedEvent)="onUxuiActionQueued($event)" />
                }
              }
              @defer (when projectShellRail() === 'token-usage') {
                @if (projectShellRail() === 'token-usage') {
                  <app-project-token-usage-panel
                    [projectName]="projShell" />
                }
              }
              @defer (when projectShellRail() === 'observability') {
                @if (projectShellRail() === 'observability') {
                  <app-project-observability-panel
                    [projectName]="projShell" />
                }
              }
              @defer (when projectShellRail() === 'product-runtime') {
                @if (projectShellRail() === 'product-runtime') {
                  <app-project-product-runtime-panel
                    [projectName]="projShell" />
                }
              }
              @defer (when projectShellRail() === 'steering') {
                @if (projectShellRail() === 'steering') {
                  <app-project-steering-docs-section
                    [projectName]="projShell" />
                }
              }
            </app-project-shell>
          </div>
        </div>
      }

      @if (analysisReportFocus(); as f) {
        <div class="overlay" data-testid="analysis-report-overlay" (click)="closeAnalysisReport()">
          <div class="overlay__panel" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeAnalysisReport()" title="Close">×</button>
            <app-analysis-report-drilldown
              [projectName]="f.project"
              [reportId]="f.reportId"
              (close)="closeAnalysisReport()" />
          </div>
        </div>
      }

      @if (workspaceTokensOpen()) {
        <div class="overlay" data-testid="workspace-tokens-overlay" (click)="closeWorkspaceTokens()">
          <div class="overlay__panel overlay__panel--wtt" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeWorkspaceTokens()" title="Close">×</button>
            <app-workspace-token-timeline />
          </div>
        </div>
      }

      @if (workspaceScreenshotsOpen()) {
        <div class="overlay" data-testid="workspace-screenshots-overlay" (click)="closeWorkspaceScreenshots()">
          <div class="overlay__panel overlay__panel--wtt" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeWorkspaceScreenshots()" title="Close">×</button>
            <app-workspace-screenshots (openTask)="onOpenTaskFromReel($event)" />
          </div>
        </div>
      }

      @if (cliAdminOpen()) {
        <div class="overlay" data-testid="cli-admin-overlay" (click)="closeCliAdmin()">
          <div class="overlay__panel" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeCliAdmin()" title="Close">×</button>
            @defer {
              <app-cli-admin-panel />
            }
          </div>
        </div>
      }

      @if (showCreate()) {
        <app-create-job-dialog
          [title]="createDialogTitle()"
          [watchPaths]="watchPaths()"
          [availableModels]="availableModels()"
          [cliTypeDraft]="newCliType"
          [(newTitle)]="newTitle"
          [(newWatchPath)]="newWatchPath"
          [(newModel)]="newModel"
          [(newPrompt)]="newPrompt"
          [(attachments)]="newAttachments"
          [(newTaskType)]="newTaskType"
          [(newTags)]="newTags"
          (cliTypeChange)="onCreateCliTypeChange($event)"
          (cancel)="cancelCreate()"
          (submit)="submitCreate()" />
      }

      @if (showUpdateStable()) {
        <app-update-stable-console (closed)="showUpdateStable.set(false)" />
      }

      @if (showE2ECleanup()) {
        <app-e2e-cleanup-dialog
          (closed)="showE2ECleanup.set(false)"
          (didDelete)="onE2EDidDelete()" />
      }

      @if (errorDialog.activeError(); as error) {
        <app-error-dialog
          [error]="error"
          [canOpenCliConfig]="error.canOpenCliConfig && selectedJobUsesCopilot()"
          [copyButtonLabel]="copyErrorButtonLabel()"
          (close)="closeErrorDialog()"
          (copy)="copyErrorDetails()"
          (openCliConfig)="openCliConfigFromError()" />
      }

      @defer (when verboseDebugContext() !== null) {
        @if (verboseDebugContext(); as ctx) {
          <app-verbose-debug-overlay
            data-testid="app-verbose-debug-overlay"
            [lines]="ctx.lines"
            [runTimeline]="ctx.runTimeline"
            [screenshots]="ctx.screenshots"
            [tokenSummary]="ctx.tokenSummary"
            [job]="ctx.job"
            [source]="ctx.job?.id ?? 'cli-output.log'"
            (close)="closeVerboseDebug()" />
        }
      }
    </div>
  `,
  styles: [`
    .app {
      /* Use 100% of body's content box rather than 100vh so the dev-mode
         banner (22px padding-top on body) doesn't push the status bar
         below the viewport. styles.scss ensures html/body fill 100% and
         box-sizing makes padding subtract from this. */
      height: 100%;
      background: #0f0f1a;
      color: #e2e8f0;
      font-family: 'Segoe UI', system-ui, sans-serif;
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }
    /* Body row holds the main layout and the CLI Usage sidesheet side-by-side.
       The sheet's :host width animates from 0 to its open width, so the layout
       reflows around it instead of being covered by an overlay.
       Body scrolls within the fixed header + status bar shell. */
    .app__body {
      flex: 1 1 auto;
      display: flex;
      flex-direction: row;
      align-items: stretch;
      min-height: 0;
      overflow: auto;
    }
    .app__body > .layout { flex: 1 1 auto; min-width: 0; }
    .app__sidesheet { align-self: stretch; }
    .header {
      flex: 0 0 auto;
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
      padding: 4px 12px;
      background: #181825;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      min-height: 36px;
    }
    .header__brand {
      display: flex;
      align-items: center;
      gap: 8px;
      flex: 0 0 auto;
    }
    .header__icon {
      width: 20px;
      height: 20px;
      display: block;
      border-radius: 5px;
      box-shadow: 0 1px 4px rgba(99,102,241,0.30);
    }
    .header__title {
      margin: 0;
      font-size: 13px;
      font-weight: 800;
      letter-spacing: -0.01em;
      display: inline-flex;
      align-items: baseline;
      gap: 0;
      line-height: 1;
    }
    .header__title-ai {
      font-family: 'Segoe UI', system-ui, sans-serif;
      font-weight: 900;
      font-style: italic;
      letter-spacing: 0.02em;
      background: linear-gradient(135deg, #a5b4fc 0%, #818cf8 40%, #c4b5fd 100%);
      -webkit-background-clip: text;
      background-clip: text;
      color: transparent;
      text-shadow: 0 0 16px rgba(167,139,250,0.30);
      padding-right: 3px;
    }
    .header__title-sep {
      width: 2px;
      height: 12px;
      align-self: center;
      margin: 0 6px;
      border-radius: 2px;
      background: linear-gradient(180deg, rgba(129,140,248,0.0), rgba(129,140,248,0.85), rgba(129,140,248,0.0));
    }
    .header__title-name {
      font-weight: 600;
      letter-spacing: 0.02em;
      color: #e2e8f0;
      text-transform: uppercase;
      font-size: 11px;
    }
    .header__subtitle { font-size: 11px; color: #64748b; }
    .header__actions { display: flex; gap: 6px; flex: 0 0 auto; align-items: center; }
    .client-filter {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      color: #cbd5e1;
      padding: 0 6px;
    }
    .client-filter__label {
      letter-spacing: 0.04em;
      text-transform: uppercase;
      font-weight: 600;
      font-size: 11px;
      color: #94a3b8;
    }
    .client-filter__select {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.10);
      color: #e2e8f0;
      border-radius: 6px;
      padding: 4px 8px;
      font-size: 12px;
      max-width: 220px;
    }
    .client-filter__select:focus {
      outline: 1px solid rgba(139,92,246,0.6);
      outline-offset: 1px;
    }
    /* Active-filter pill strip: a single line below the header showing every
       active filter (Owner / Project / Type / Tags) with a per-pill × and a
       trailing "Clear all". Collapses to zero height when no filter is set. */
    .active-filter-strip {
      flex: 0 0 auto;
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      padding: 6px 12px;
      background: rgba(15, 15, 26, 0.60);
      border-bottom: 1px solid rgba(255, 255, 255, 0.06);
      font-size: 11px;
    }
    .active-filter-strip__pill {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      background: rgba(255, 255, 255, 0.04);
      border: 1px solid rgba(255, 255, 255, 0.14);
      color: #e2e8f0;
      border-radius: 999px;
      padding: 2px 4px 2px 8px;
      font-weight: 600;
    }
    .active-filter-strip__pill--owner {
      background: rgba(56, 189, 248, 0.10);
      border-color: rgba(56, 189, 248, 0.45);
      color: #bae6fd;
    }
    .active-filter-strip__pill--project {
      background: rgba(167, 139, 250, 0.12);
      border-color: rgba(167, 139, 250, 0.55);
      color: #ddd6fe;
    }
    .active-filter-strip__pill--type {
      background: rgba(139, 92, 246, 0.18);
      border-color: rgba(139, 92, 246, 0.55);
      color: #ddd6fe;
    }
    .active-filter-strip__pill--tag {
      background: rgba(255, 255, 255, 0.04);
      border-color: rgba(255, 255, 255, 0.20);
      color: #f1f5f9;
    }
    .active-filter-strip__pill-kind {
      font-size: 10px;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: #94a3b8;
      font-weight: 700;
    }
    .active-filter-strip__pill-swatch {
      width: 10px;
      height: 10px;
      border-radius: 999px;
      border: 1px solid rgba(255, 255, 255, 0.20);
    }
    .active-filter-strip__pill-label {
      max-width: 220px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .active-filter-strip__pill-remove {
      background: transparent;
      border: 0;
      color: inherit;
      opacity: 0.65;
      cursor: pointer;
      width: 18px;
      height: 18px;
      border-radius: 999px;
      display: grid;
      place-items: center;
      font-size: 14px;
      line-height: 1;
      padding: 0;
    }
    .active-filter-strip__pill-remove:hover {
      opacity: 1;
      background: rgba(255, 255, 255, 0.08);
    }
    .active-filter-strip__clear-all {
      margin-left: auto;
      background: transparent;
      border: 1px solid rgba(248, 113, 113, 0.30);
      color: #fca5a5;
      border-radius: 8px;
      padding: 2px 10px;
      font-size: 11px;
      font-weight: 600;
      cursor: pointer;
    }
    .active-filter-strip__clear-all:hover {
      background: rgba(248, 113, 113, 0.10);
      border-color: rgba(248, 113, 113, 0.55);
      color: #fecaca;
    }
    .header__filters { display: flex; gap: 6px; align-items: center; }
    /* Filter chip carries each project's identity colour as a CSS variable
       supplied per chip; the active state pulls the chip into the project's
       hue so a five-to-ten-project header is scannable at a glance. */
    .filter-chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      background: rgba(255,255,255,0.06);
      border: 1px solid var(--project-border, rgba(255,255,255,0.20));
      color: #e2e8f0;
      padding: 4px 12px 4px 4px;
      border-radius: 20px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      transition: all 0.15s;
    }
    .filter-chip__disk {
      display: inline-grid;
      place-items: center;
      width: 18px;
      height: 18px;
      border-radius: 999px;
      background: var(--project-color, #8b5cf6);
      color: var(--project-on, #0b1020);
      font-size: 11px;
      font-weight: 800;
      flex: 0 0 auto;
    }
    .filter-chip:hover {
      background: var(--project-soft, rgba(255,255,255,0.18));
      border-color: var(--project-border, rgba(255,255,255,0.30));
      color: #ffffff;
    }
    .filter-chip--active {
      background: var(--project-soft, rgba(139,92,246,0.45));
      border-color: var(--project-border, rgba(167,139,250,0.85));
      color: #ffffff;
      box-shadow: 0 0 0 1px var(--project-border, rgba(167,139,250,0.25)), 0 2px 6px rgba(0,0,0,0.30);
    }
    .filter-chip--active:hover {
      background: var(--project-soft, rgba(139,92,246,0.6));
      filter: brightness(1.15);
    }
    .runner-dot { font-size: 10px; margin-right: 2px; }
    .runner-dot--running { animation: pulse-runner 1.5s infinite; }
    /* Per-project token total badge in the chip. Aggregates every job's
       token count for the project so the user sees AI spend at the board
       level without opening any drilldown. The badge only renders when the
       project has accumulated tokens, so projects without any AI activity
       stay visually quiet. Tooltip carries the per-input/output split,
       cache amounts, and the model list. */
    .filter-chip__tokens {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      margin-left: 4px;
      padding: 1px 6px 1px 5px;
      border-radius: 999px;
      background: rgba(148, 163, 184, 0.18);
      color: #cbd5e1;
      font-size: 10px;
      font-weight: 600;
      font-variant-numeric: tabular-nums;
      letter-spacing: 0.02em;
      cursor: help;
    }
    .filter-chip--active .filter-chip__tokens {
      background: rgba(255, 255, 255, 0.18);
      color: #ffffff;
    }
    .filter-chip__tokens-icon {
      opacity: 0.7;
      font-size: 9px;
    }
    @keyframes pulse-runner {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.4; }
    }
    .project-tab {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .project-tab__detail,
    .project-tab__shell {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
      color: rgba(255,255,255,0.55);
      border-radius: 6px;
      width: 26px;
      height: 26px;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      padding: 0;
    }
    .project-tab__detail:hover,
    .project-tab__shell:hover {
      background: rgba(255,255,255,0.10);
      color: #cdd6f4;
    }
    .auto-toggle {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.12);
      color: #94a3b8;
      padding: 4px 10px;
      border-radius: 16px;
      cursor: pointer;
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.02em;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .auto-toggle:hover {
      background: rgba(255,255,255,0.10);
      border-color: rgba(255,255,255,0.22);
      color: #e2e8f0;
    }
    .auto-toggle__icon {
      font-size: 11px;
      line-height: 1;
    }
    .auto-toggle__count {
      background: rgba(255,255,255,0.12);
      border-radius: 999px;
      padding: 1px 6px;
      font-size: 10px;
      font-weight: 700;
      color: #f8fafc;
      min-width: 16px;
      text-align: center;
    }
    .auto-toggle--on {
      background: rgba(34,197,94,0.18);
      border-color: rgba(74,222,128,0.55);
      color: #bbf7d0;
      box-shadow: 0 0 0 1px rgba(74,222,128,0.18);
    }
    .auto-toggle--on:hover {
      background: rgba(34,197,94,0.28);
      border-color: rgba(74,222,128,0.75);
      color: #f0fdf4;
    }
    .auto-toggle--on .auto-toggle__count {
      background: rgba(74,222,128,0.30);
      color: #f0fdf4;
    }
    .auto-toggle--stopping {
      background: rgba(234,179,8,0.18);
      border-color: rgba(250,204,21,0.55);
      color: #fde68a;
      box-shadow: 0 0 0 1px rgba(250,204,21,0.18);
      animation: auto-stopping-pulse 2s ease-in-out infinite;
    }
    .auto-toggle--stopping:hover {
      background: rgba(234,179,8,0.28);
      border-color: rgba(250,204,21,0.75);
      color: #fef9c3;
    }
    @keyframes auto-stopping-pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.65; }
    }
    .btn {
      background: rgba(255,255,255,0.10);
      border: 1px solid rgba(255,255,255,0.20);
      color: #f8fafc;
      padding: 6px 14px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      transition: background 0.15s, border-color 0.15s;
    }
    .btn:hover { background: rgba(255,255,255,0.18); border-color: rgba(255,255,255,0.30); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .btn--create {
      background: rgba(139,92,246,0.45);
      border-color: rgba(167,139,250,0.85);
      color: #ffffff;
      box-shadow: 0 1px 4px rgba(139,92,246,0.30);
    }
    .btn--create:hover { background: rgba(139,92,246,0.6); border-color: rgba(196,181,253,0.95); }
    .btn--compact-toggle {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
    }
    .btn--compact-toggle--active {
      background: rgba(56,189,248,0.20);
      border-color: rgba(56,189,248,0.55);
      color: #bae6fd;
    }
    .btn--compact-toggle--active:hover {
      background: rgba(56,189,248,0.28);
      border-color: rgba(56,189,248,0.75);
    }
    .btn--compact-toggle__label { font-weight: 600; }
    .header__filter-trigger {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 4px 10px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 6px;
      color: #cbd5e1;
      cursor: pointer;
      font-size: 12px;
      transition: background 0.12s, color 0.12s, border-color 0.12s;
    }
    .header__filter-trigger:hover {
      background: rgba(255,255,255,0.10);
      color: #f8fafc;
    }
    .header__filter-trigger--active {
      background: rgba(59,130,246,0.20);
      border-color: rgba(96,165,250,0.55);
      color: #bfdbfe;
    }
    .header__filter-trigger--active:hover {
      background: rgba(59,130,246,0.30);
      border-color: rgba(147,197,253,0.75);
    }
    .header__filter-trigger__chip {
      max-width: 160px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-style: italic;
      color: inherit;
    }
    .header__filter-trigger__count {
      min-width: 18px;
      padding: 0 6px;
      height: 18px;
      border-radius: 999px;
      background: rgba(99,102,241,0.55);
      color: #ffffff;
      font-size: 11px;
      font-weight: 700;
      display: inline-flex;
      align-items: center;
      justify-content: center;
    }
    .devtools-menu { position: relative; display: inline-flex; }
    .devtools-menu__trigger {
      background: transparent;
      border: 1px solid rgba(255,255,255,0.10);
      color: #94a3b8;
      width: 28px;
      height: 28px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 18px;
      line-height: 1;
      padding: 0;
      display: grid;
      place-items: center;
      transition: background 0.15s, color 0.15s, border-color 0.15s;
    }
    .devtools-menu__trigger:hover {
      background: rgba(255,255,255,0.08);
      border-color: rgba(255,255,255,0.18);
      color: #e2e8f0;
    }
    .devtools-menu__trigger--open {
      background: rgba(255,255,255,0.10);
      border-color: rgba(255,255,255,0.22);
      color: #f8fafc;
    }
    .devtools-menu__backdrop {
      position: fixed;
      inset: 0;
      z-index: 90;
      background: transparent;
    }
    .devtools-menu__panel {
      position: absolute;
      top: calc(100% + 6px);
      right: 0;
      z-index: 100;
      min-width: 240px;
      background: #181825;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 10px;
      box-shadow: 0 12px 40px rgba(0,0,0,0.45);
      padding: 6px;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .devtools-menu__header {
      padding: 6px 10px 4px;
      font-size: 10px;
      letter-spacing: 0.1em;
      text-transform: uppercase;
      color: #64748b;
    }
    .devtools-menu__item {
      display: grid;
      grid-template-columns: 18px 1fr;
      grid-template-rows: auto auto;
      column-gap: 10px;
      align-items: center;
      background: transparent;
      border: 0;
      color: #e2e8f0;
      padding: 8px 10px;
      border-radius: 6px;
      cursor: pointer;
      text-align: left;
      font-family: inherit;
    }
    .devtools-menu__item:hover { background: rgba(255,255,255,0.06); }
    .devtools-menu__icon { grid-row: 1 / span 2; font-size: 14px; }
    .devtools-menu__label { font-size: 13px; font-weight: 600; }
    .devtools-menu__hint { font-size: 11px; color: #64748b; grid-column: 2; }
    .devtools-menu__item--danger .devtools-menu__label { color: #fecaca; }
    .devtools-menu__item--danger:hover { background: rgba(244,63,94,0.14); }
    .btn--primary {
      background: #6366f1;
      border-color: #818cf8;
      color: white;
      font-weight: 600;
    }
    .btn--primary:hover { background: #5558e6; border-color: #a5b4fc; }

    .overlay {
      position: fixed;
      inset: 0;
      background: rgba(0,0,0,0.6);
      display: grid;
      place-items: center;
      z-index: 100;
    }
    .overlay__panel {
      position: relative;
      background: #1e1e2e;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 16px;
      width: min(960px, 94vw);
      max-height: 90vh;
      overflow-y: auto;
    }
    .overlay__panel--wtt {
      width: min(1080px, 96vw);
    }
    /*
     * The project-page shell renders full-bleed instead of as a centred
     * dialog. It owns the whole viewport so the left rail can reach the
     * edges and the content area gets real estate to grow into. The
     * .overlay--shell modifier disables the grid centring and skips the
     * panel border + max-width so the shell paints edge-to-edge.
     */
    .overlay--shell {
      display: block;
      background: #181825;
    }
    .overlay__shell-panel {
      position: absolute;
      inset: 0;
      display: flex;
    }
    .overlay__shell-panel > app-project-shell {
      flex: 1;
      min-width: 0;
      min-height: 0;
    }
    .overlay__close {
      position: absolute;
      top: 8px;
      right: 8px;
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12);
      border-radius: 6px;
      width: 28px;
      height: 28px;
      cursor: pointer;
      font-size: 18px;
      line-height: 1;
      z-index: 1;
    }
    .overlay__close:hover { background: rgba(255,255,255,0.12); }
    .create-dialog {
      position: relative;
      background: #1e1e2e;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 16px;
      padding: 32px;
      width: min(820px, 92vw);
      max-height: 90vh;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .create-dialog__close {
      position: absolute;
      top: 12px;
      right: 12px;
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12);
      border-radius: 6px;
      width: 28px;
      height: 28px;
      cursor: pointer;
      font-size: 18px;
      line-height: 1;
      display: grid;
      place-items: center;
      padding: 0;
    }
    .create-dialog__close:hover { background: rgba(255,255,255,0.12); }
    .create-dialog__title {
      margin: 0 0 12px;
      font-size: 22px;
      color: #f8fafc;
    }
    .create-dialog .field__textarea {
      min-height: 220px;
      resize: vertical;
    }
    .create-dialog--drag {
      box-shadow: 0 0 0 2px rgba(56,189,248,0.55), 0 24px 80px rgba(0,0,0,0.45);
      border-color: rgba(56,189,248,0.7);
    }
    .create-dialog__prompt-label {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      margin-bottom: 4px;
    }
    .create-dialog__attach-btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #cbd5e1;
      padding: 4px 10px;
      border-radius: 6px;
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
      transition: background 0.15s, color 0.15s, border-color 0.15s;
    }
    .create-dialog__attach-btn:hover {
      background: rgba(99,102,241,0.18);
      color: #ddd6fe;
      border-color: rgba(167,139,250,0.55);
    }
    .create-dialog__attachments {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 8px;
    }
    .create-dialog__attachment {
      position: relative;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 4px;
      padding: 6px 8px 8px;
      background: rgba(0,0,0,0.25);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      max-width: 140px;
    }
    .create-dialog__attachment img {
      width: 120px;
      height: 80px;
      object-fit: cover;
      border-radius: 4px;
      border: 1px solid rgba(255,255,255,0.08);
    }
    .create-dialog__attachment-name {
      font-size: 11px;
      color: #94a3b8;
      max-width: 120px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .create-dialog__attachment-remove {
      position: absolute;
      top: 2px;
      right: 4px;
      width: 20px;
      height: 20px;
      border-radius: 999px;
      border: 0;
      background: rgba(0,0,0,0.55);
      color: #f8fafc;
      font-size: 14px;
      line-height: 1;
      cursor: pointer;
    }
    .create-dialog__attachment-remove:hover {
      background: rgba(239,68,68,0.7);
    }
    .create-dialog__attachment-error {
      margin-top: 6px;
      font-size: 12px;
      color: #fca5a5;
      background: rgba(239,68,68,0.10);
      border: 1px solid rgba(239,68,68,0.35);
      border-radius: 6px;
      padding: 6px 10px;
    }
    .create-dialog__file {
      display: none;
    }
    .create-dialog__title-row {
      display: flex;
      gap: 8px;
      align-items: stretch;
    }
    .create-dialog__title-input {
      flex: 1 1 auto;
      min-width: 0;
    }
    .create-dialog__generate-btn {
      flex: 0 0 auto;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      background: rgba(99,102,241,0.18);
      border: 1px solid rgba(167,139,250,0.45);
      color: #ddd6fe;
      padding: 0 12px;
      border-radius: 6px;
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
      transition: background 0.15s, color 0.15s, border-color 0.15s, opacity 0.15s;
      white-space: nowrap;
    }
    .create-dialog__generate-btn:hover:not(:disabled) {
      background: rgba(99,102,241,0.32);
      color: #f5f3ff;
      border-color: rgba(167,139,250,0.75);
    }
    .create-dialog__generate-btn:disabled {
      opacity: 0.45;
      cursor: not-allowed;
    }
    .create-dialog__generate-spinner {
      width: 12px;
      height: 12px;
      border-radius: 999px;
      border: 2px solid rgba(221,214,254,0.35);
      border-top-color: #ddd6fe;
      animation: create-dialog-spin 0.8s linear infinite;
    }
    @keyframes create-dialog-spin {
      to { transform: rotate(360deg); }
    }
    .create-dialog__generate-error {
      margin-top: 6px;
      font-size: 12px;
      color: #fca5a5;
      background: rgba(239,68,68,0.10);
      border: 1px solid rgba(239,68,68,0.35);
      border-radius: 6px;
      padding: 6px 10px;
    }
    .create-dialog__prompt-actions {
      display: inline-flex;
      align-items: center;
      gap: 8px;
    }
    .create-dialog__enhance-preview {
      margin-top: 10px;
      padding: 12px;
      border-radius: 8px;
      background: rgba(99,102,241,0.08);
      border: 1px solid rgba(167,139,250,0.30);
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .create-dialog__enhance-row {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .create-dialog__enhance-label {
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      color: #a5b4fc;
    }
    .create-dialog__enhance-refined {
      margin: 0;
      font-family: inherit;
      font-size: 13px;
      line-height: 1.5;
      color: #e2e8f0;
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 200px;
      overflow-y: auto;
    }
    .create-dialog__enhance-intent {
      font-size: 13px;
      color: #e2e8f0;
    }
    .create-dialog__enhance-tags {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .create-dialog__enhance-tag {
      display: inline-block;
      padding: 2px 8px;
      border-radius: 999px;
      font-size: 11px;
      font-weight: 600;
      background: rgba(167,139,250,0.18);
      color: #ddd6fe;
      border: 1px solid rgba(167,139,250,0.40);
    }
    .create-dialog__enhance-actions {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      margin-top: 4px;
    }
    .overlay--error {
      z-index: 120;
      padding: 24px;
      align-items: start;
      overflow-y: auto;
    }
    .error-dialog {
      background: #11111b;
      border: 1px solid rgba(248,113,113,0.28);
      border-radius: 18px;
      padding: 24px;
      width: min(860px, 100%);
      box-shadow: 0 24px 80px rgba(0,0,0,0.45);
      display: flex;
      flex-direction: column;
      gap: 16px;
    }
    .error-dialog__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .error-dialog__eyebrow {
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #fca5a5;
      margin-bottom: 6px;
    }
    .error-dialog__title {
      margin: 0;
      font-size: 22px;
      color: #ffe4e6;
    }
    .error-dialog__close {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #f8fafc;
      width: 36px;
      height: 36px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 16px;
    }
    .error-dialog__close:hover { background: rgba(255,255,255,0.1); }
    .error-dialog__source {
      font-size: 12px;
      color: #fda4af;
      padding: 8px 10px;
      border-radius: 10px;
      background: rgba(244,63,94,0.08);
      border: 1px solid rgba(244,63,94,0.18);
      width: fit-content;
      max-width: 100%;
      word-break: break-word;
    }
    .error-dialog__message {
      font-size: 15px;
      line-height: 1.6;
      color: #ffe4e6;
      padding: 14px 16px;
      border-radius: 14px;
      background: rgba(244,63,94,0.1);
      border: 1px solid rgba(244,63,94,0.18);
    }
    .error-dialog__actions {
      display: flex;
      justify-content: flex-end;
      gap: 10px;
      flex-wrap: wrap;
    }
    .error-dialog__section {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .error-dialog__section-title {
      font-size: 12px;
      color: #94a3b8;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      font-weight: 700;
    }
    .error-dialog__code {
      margin: 0;
      padding: 16px;
      border-radius: 14px;
      background: rgba(0,0,0,0.32);
      border: 1px solid rgba(255,255,255,0.08);
      color: #e2e8f0;
      font-size: 12px;
      line-height: 1.55;
      font-family: 'Consolas', 'SFMono-Regular', monospace;
      overflow: auto;
      max-height: 280px;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .error-dialog__empty {
      padding: 14px 16px;
      border-radius: 14px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.06);
      color: #94a3b8;
      font-size: 13px;
    }
    .create-dialog__title { margin: 0 0 20px; font-size: 18px; }
    .create-dialog__actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .create-cli-picker {
      display: inline-flex;
      gap: 2px;
      padding: 2px;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 8px;
      background: rgba(0,0,0,0.3);
    }
    .create-cli-picker__btn {
      border: 0;
      background: transparent;
      color: #94a3b8;
      padding: 5px 14px;
      font-size: 13px;
      border-radius: 6px;
      cursor: pointer;
      transition: background 0.15s, color 0.15s;
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .create-cli-picker__icon { font-size: 14px; line-height: 1; }
    .create-cli-picker__btn:hover { color: #e2e8f0; background: rgba(255,255,255,0.06); }
    .create-cli-picker__btn--active {
      background: rgba(99,102,241,0.22);
      color: #c7d2fe;
    }
    .create-type-picker {
      display: inline-flex;
      gap: 2px;
      padding: 2px;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 8px;
      background: rgba(0,0,0,0.3);
    }
    .create-type-picker__btn {
      border: 0;
      background: transparent;
      color: #94a3b8;
      padding: 5px 12px;
      font-size: 12px;
      border-radius: 6px;
      cursor: pointer;
      transition: background 0.15s, color 0.15s;
    }
    .create-type-picker__btn:hover { color: #e2e8f0; background: rgba(255,255,255,0.06); }
    .create-type-picker__btn--active { background: rgba(139,92,246,0.22); color: #ddd6fe; }
    .create-tag-picker { display: flex; flex-wrap: wrap; gap: 6px; }
    .create-tag-picker__chip {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
      color: #cbd5e1;
      border-radius: 999px;
      padding: 3px 9px;
      font-size: 11px;
      cursor: pointer;
      font-weight: 500;
    }
    .create-tag-picker__chip--active {
      background: color-mix(in srgb, var(--tag-color, #94a3b8) 22%, rgba(0,0,0,0));
      border-color: var(--tag-color, #94a3b8);
      color: #f1f5f9;
    }
    .field { display: flex; flex-direction: column; gap: 4px; margin-bottom: 12px; }
    .field__label { font-size: 12px; color: #94a3b8; text-transform: uppercase; letter-spacing: 0.5px; }
    .field__input {
      background: rgba(0,0,0,0.3);
      border: 1px solid rgba(255,255,255,0.1);
      color: #e2e8f0;
      padding: 8px 12px;
      border-radius: 8px;
      font-size: 13px;
    }
    .field__input:focus { outline: none; border-color: #6366f1; }
    .field__textarea { font-family: 'Consolas', monospace; resize: vertical; }

    /* Layout fills the scroll container; the app__body now provides
       the scrollbar so the header and status bar stay pinned. */
    .layout {
      min-height: 100%;
      transition: all 0.3s ease;
      display: flex;
      flex-direction: column;
    }
    .layout--focus {
      padding: 12px;
    }
    .dashboard {
      display: flex;
      gap: 18px;
      padding: 16px;
      overflow-x: auto;
      flex: 1;
      min-width: 0;
      align-items: stretch;
    }
    /*
     * ADR-0028 persistent banner. Always rendered when at least one job
     * lives in 3a-failed-pickup across the visible (filtered) board, so
     * pickup failures cannot be hidden by collapsing the lane or by
     * filtering the project list. Sits between the workspace banner and
     * the kanban dashboard. Single amber outline tint matches the
     * failed-pickup lane's treatment per kanban-board-design taxonomy.
     */
    .failed-pickup-banner {
      display: flex;
      align-items: center;
      gap: 10px;
      width: calc(100% - 32px);
      margin: 12px 16px 0;
      padding: 10px 14px;
      background: rgba(245, 158, 11, 0.08);
      border: 1px solid rgba(245, 158, 11, 0.55);
      border-radius: 10px;
      color: #fcd34d;
      font-size: 13px;
      cursor: pointer;
      text-align: left;
      transition: background 0.15s, border-color 0.15s;
    }
    .failed-pickup-banner:hover {
      background: rgba(245, 158, 11, 0.16);
      border-color: rgba(245, 158, 11, 0.85);
    }
    .failed-pickup-banner__dot {
      width: 12px;
      height: 12px;
      border-radius: 999px;
      background: #f59e0b;
      box-shadow: 0 0 0 2px rgba(245, 158, 11, 0.20);
      flex-shrink: 0;
    }
    .failed-pickup-banner__text { flex: 1 1 auto; min-width: 0; }
    .failed-pickup-banner__text strong {
      color: #fbbf24;
      font-weight: 700;
      margin-right: 2px;
    }
    .failed-pickup-banner__chev {
      color: #fbbf24;
      font-size: 18px;
      line-height: 1;
      flex-shrink: 0;
    }
    /* Banner click-through pulse: highlights the failed-pickup lane briefly
       so the user's eye lands on it after the smooth scroll completes. */
    .column--failed-pickup-pulse {
      animation: failed-pickup-pulse 1.4s ease-out 1;
    }
    @keyframes failed-pickup-pulse {
      0%   { box-shadow: 0 0 0 0 rgba(245, 158, 11, 0.0); }
      30%  { box-shadow: 0 0 0 6px rgba(245, 158, 11, 0.40); }
      100% { box-shadow: 0 0 0 0 rgba(245, 158, 11, 0.0); }
    }
    @media (prefers-reduced-motion: reduce) {
      .column--failed-pickup-pulse { animation: none; }
      .failed-pickup-banner { transition: none; }
    }
    /*
     * Lane groups bundle the individual columns into visually distinct
     * phases of the workflow:
     *   Backlog (human)    -> Preparation, Ready
     *   Active (agent)     -> In Progress, Review
     *   Done               -> Completed, Archive
     * The group is just a contiguous flex container with a small header.
     * It does not eat horizontal space when the inner lanes collapse, so
     * the overall board reflows the same way it always did.
     */
    .lane-group {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 6px 6px 0;
      border: 1px solid rgba(255,255,255,0.04);
      border-radius: 18px;
      background: rgba(255,255,255,0.015);
      /*
       * Lane groups grow to share the dashboard's horizontal space so
       * the board fills 100% of the viewport. Inside each group, the
       * .column children are flex: 1 1 220px and split the group's
       * width between them. Below the sum-of-min-widths the dashboard's
       * overflow-x: auto scrolls horizontally.
       *
       * flex-grow is overridden inline in the template via
       * [style.flex-grow]="expandedLaneCount(g)" so every group grows
       * in proportion to the number of expanded lanes it owns. With a
       * uniform flex-grow: 1 the dashboard's leftover horizontal space
       * was split evenly across the three groups, which then divided
       * their share over different lane counts: lanes inside smaller
       * groups (e.g. Ready when Backlog held three expanded lanes,
       * vs. five elsewhere) ended up visibly wider than lanes
       * elsewhere on the board, with a phantom horizontal scroll
       * indicator at the bottom of the dashboard once the resulting
       * sub-pixel rounding pushed total content width past viewport.
       *
       * flex-basis is 0 (not auto): with auto the basis is the
       * group's preferred content width, and the content is dominated
       * by per-lane card max-content sizes that vary across groups
       * (Auto Review with running cards is wider than In Preparation
       * with placeholder copy). Equal-share growth then produced
       * unequal final widths because the bases were unequal. With
       * basis: 0 the entire dashboard width is distributed by
       * flex-grow alone, so every expanded lane lands on the same
       * width regardless of which group it lives in.
       *
       * No min-width: 0 here. Default min-width: auto on a flex item
       * resolves to its min-content size, which is the sum of the
       * inner .column min-widths plus gaps. Forcing min-width: 0
       * lets the inner columns visually leak past the group's box
       * into the next group's space (the lane-overlap symptom);
       * preserving the auto value keeps each group at least as wide
       * as its lanes need and pushes the overflow into the
       * dashboard's horizontal scroll, where it belongs.
       */
      flex: 1 1 0;
    }
    .lane-group__head {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 4px 8px 0;
      color: #94a3b8;
      font-size: 11px;
      letter-spacing: 0.06em;
      text-transform: uppercase;
    }
    .lane-group__label {
      font-weight: 700;
      color: #cbd5e1;
    }
    .lane-group__toggle,
    .lane-group__focus {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.06);
      color: #94a3b8;
      width: 20px;
      height: 20px;
      border-radius: 3px;
      cursor: pointer;
      font-size: 11px;
      line-height: 1;
      display: grid;
      place-items: center;
      padding: 0;
      flex: 0 0 auto;
    }
    .lane-group__focus { margin-left: auto; }
    .lane-group__toggle:hover,
    .lane-group__focus:hover {
      background: rgba(255,255,255,0.10);
      color: #f8fafc;
    }
    .lane-group--focused .lane-group__focus {
      background: rgba(148,163,184,0.20);
      color: #f8fafc;
      border-color: rgba(148,163,184,0.35);
    }
    .lane-group__strip {
      display: flex;
      align-items: center;
      gap: 6px;
      flex-wrap: nowrap;
      overflow-x: auto;
      min-height: 24px;
      padding: 0 4px;
      flex: 1 1 auto;
      text-transform: none;
      letter-spacing: 0;
    }
    .lane-group__chip {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 3px;
      padding: 2px 6px;
      font-size: 11px;
      color: #cbd5e1;
      white-space: nowrap;
    }
    .lane-group__chip-icon { font-size: 12px; line-height: 1; }
    .lane-group__chip-count {
      color: #94a3b8;
      font-variant-numeric: tabular-nums;
    }
    .lane-group--collapsed {
      flex: 0 0 auto;
    }
    .lane-group--focused {
      flex: 1 1 100%;
    }
    .lane-group__lanes {
      display: flex;
      gap: 12px;
      flex: 1;
      align-items: stretch;
      /* No min-width: 0. See the .lane-group block above for the
         reasoning - the inner .column elements have min-width: 220px
         and their sum (plus gaps) is the natural floor of this row.
         Allowing it to shrink below that floor is exactly the
         lane-overlap regression. */
    }
    /* The app-job-column host is transparent to flex layout so the
       inner .column div participates as the actual flex item with its
       own flex: 1 1 220px rule. Without this the host's default
       inline-block sizing pinned columns to their content width and
       left empty space at the right of the dashboard. */
    .lane-group__lanes > app-job-column { display: contents; }
    .workspace {
      display: grid;
      grid-template-columns: var(--side-sheet-width, 280px) minmax(0, 1fr);
      gap: 16px;
      width: 100%;
      flex: 1 1 auto;
      min-height: 0;
      align-items: stretch;
      animation: slideIn 0.25s ease;
      position: relative;
    }
    .workspace--nav-collapsed {
      grid-template-columns: 36px minmax(0, 1fr);
      gap: 12px;
    }
    .task-nav.task-nav--collapsed {
      padding: 10px 4px;
      gap: 8px;
      min-width: 0;
      width: 36px;
      align-items: center;
      overflow: hidden;
    }
    .task-nav__header-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    .task-nav__collapse,
    .task-nav__expand {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #cbd5e1;
      width: 28px;
      height: 28px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 16px;
      line-height: 1;
      display: grid;
      place-items: center;
    }
    .task-nav__collapse:hover,
    .task-nav__expand:hover {
      background: rgba(255,255,255,0.12);
      color: #f8fafc;
    }
    .task-nav__expand--board {
      margin-top: 4px;
    }
    .workspace__main {
      display: flex;
      min-width: 0;
      min-height: 0;
      position: relative;
    }
    .workspace__main > app-job-detail {
      display: block;
      flex: 1;
      min-width: 0;
      min-height: 0;
    }
    /* Triage banner shown by the auto-advance flow (lane cleared / external
       move). Sits anchored bottom-right of the detail viewport so the user
       sees it whether they hit a primary action or someone else moved the
       job out from under them. */
    .triage-toast {
      position: absolute;
      right: 24px;
      bottom: 24px;
      z-index: 60;
      padding: 8px 14px;
      border-radius: 999px;
      background: rgba(15, 23, 42, 0.92);
      border: 1px solid rgba(137, 180, 250, 0.45);
      color: #cdd6f4;
      font-size: 0.82rem;
      font-weight: 600;
      letter-spacing: 0.02em;
      box-shadow: 0 6px 20px rgba(0, 0, 0, 0.45);
      pointer-events: none;
      animation: triage-toast-fade 0.18s ease-out;
    }
    @keyframes triage-toast-fade {
      from { opacity: 0; transform: translateY(6px); }
      to   { opacity: 1; transform: translateY(0); }
    }
    .task-nav {
      background: #181825;
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 20px;
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 18px;
      height: 100%;
      max-height: none;
      overflow: hidden;
      min-width: 200px;
      box-sizing: border-box;
      position: relative;
    }
    .task-nav__header {
      display: flex;
      flex-direction: column;
      gap: 14px;
      padding-bottom: 16px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .task-nav__eyebrow {
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #64748b;
      margin-bottom: 4px;
    }
    .task-nav__title {
      margin: 0;
      font-size: 20px;
      color: #e2e8f0;
    }
    .task-nav__groups {
      display: flex;
      flex-direction: column;
      gap: 16px;
      overflow-y: auto;
      padding-right: 4px;
    }
    .task-nav__group {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .task-nav__group-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      font-size: 12px;
      color: #94a3b8;
      font-weight: 600;
      cursor: pointer;
      user-select: none;
      transition: color 0.15s ease;
    }
    .task-nav__group-header:hover {
      color: #cbd5e1;
    }
    .task-nav__count {
      background: rgba(255,255,255,0.08);
      border-radius: 999px;
      padding: 2px 8px;
      font-size: 11px;
      color: #cbd5e1;
    }
    .task-nav__items {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .task-nav__item {
      width: 100%;
      text-align: left;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.06);
      color: #cbd5e1;
      border-radius: 14px;
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 8px;
      cursor: pointer;
      transition: border-color 0.15s ease, background 0.15s ease, transform 0.15s ease;
    }
    .task-nav__item:hover {
      background: rgba(255,255,255,0.06);
      border-color: rgba(255,255,255,0.12);
      transform: translateY(-1px);
    }
    .task-nav__item--active {
      background: rgba(99,102,241,0.16);
      border-color: rgba(99,102,241,0.45);
      box-shadow: 0 0 0 1px rgba(99,102,241,0.15);
    }
    .task-nav__item-title {
      font-size: 14px;
      font-weight: 600;
      color: #f8fafc;
      line-height: 1.4;
    }
    .task-nav__item-meta {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      font-size: 11px;
      color: #94a3b8;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .task-nav__item-project {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      color: var(--project-color, #94a3b8);
    }
    .task-nav__item-disk {
      display: inline-grid;
      place-items: center;
      width: 14px;
      height: 14px;
      border-radius: 999px;
      background: var(--project-color, #94a3b8);
      color: var(--project-on, #0b1020);
      font-size: 9px;
      font-weight: 800;
      flex: 0 0 auto;
    }
    .task-nav__group-toggle {
      display: inline-block;
      width: 12px;
      font-size: 10px;
      transition: transform 0.15s ease;
      margin-right: 4px;
    }
    .task-nav__group--collapsed .task-nav__group-toggle {
      transform: rotate(0deg);
    }
    .task-nav__group--collapsed .task-nav__items {
      display: none;
    }
    .task-nav__add {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 5px;
      width: 100%;
      background: rgba(139, 92, 246, 0.06);
      border: 1px dashed rgba(139, 92, 246, 0.28);
      color: #a78bfa;
      padding: 7px 10px;
      border-radius: 10px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 500;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .task-nav__add:hover {
      background: rgba(139, 92, 246, 0.16);
      border-color: rgba(139, 92, 246, 0.5);
      color: #c4b5fd;
    }
    .task-nav__resize-handle {
      position: absolute;
      top: 0;
      right: 0;
      width: 4px;
      height: 100%;
      cursor: col-resize;
      background: transparent;
      border-radius: 0 20px 20px 0;
      transition: background 0.15s ease;
    }
    .task-nav__resize-handle:hover {
      background: rgba(99, 102, 241, 0.3);
    }
    .task-nav__resize-handle--active {
      background: rgba(99, 102, 241, 0.5);
    }
    .btn--ghost {
      justify-self: flex-start;
      width: fit-content;
      color: #cbd5e1;
    }
    @keyframes slideIn {
      from { transform: translateX(20px); opacity: 0; }
      to { transform: translateX(0); opacity: 1; }
    }
    @media (max-width: 1200px) {
      .header {
        align-items: flex-start;
        flex-wrap: wrap;
        gap: 12px;
      }
      .header__filters {
        flex-wrap: wrap;
      }
      .workspace {
        grid-template-columns: 1fr;
      }
      .task-nav {
        position: static;
        max-height: none;
      }
    }
    
    :host {
      --resizing-cursor: col-resize;
    }
    
    ::ng-deep body.resizing {
      cursor: col-resize !important;
      user-select: none;
    }
    
    ::ng-deep body.resizing * {
      cursor: col-resize !important;
    }

    /* ---------------------------------------------------------------
       VS Code-style layout (flag: Frontend:VsCodeLayout, default off)
       Spec: docs/mockups/vscode-layout/. Slice 1 reduces chrome density
       and pulls the chat to the top of the viewport without restructuring
       the DOM. Activity bar, tab bar, and full meta panel land in later
       slices; the taxonomy notes the gap.
       --------------------------------------------------------------- */
    .app--vscode-layout {
      --vscode-density-pad: 6px 12px;
      --vscode-chrome-fg: #cbd5e1;
      --vscode-chrome-bg: #181825;
    }
    /* Slimmer top header. Brand stays; dev tools menu stays. The
       project switcher disappears once a task is open — its job moves to
       the status bar in slice 1, to the activity bar in slice 3. */
    .app--vscode-layout .header {
      min-height: 30px;
      padding: 2px 10px;
    }
    .app--vscode-layout.app--task-open app-project-tabs {
      display: none;
    }
    /* Detail view: collapse the multi-row header to a single thin strip
       and tighten pane padding so the chat starts close to the top. */
    .app--vscode-layout .detail {
      gap: 0;
    }
    .app--vscode-layout .detail__header {
      padding: 2px 6px;
      align-items: center;
      gap: 8px;
      min-height: 28px;
    }
    .app--vscode-layout .detail__back {
      width: 22px;
      height: 22px;
      font-size: 12px;
      border-radius: 4px;
    }
    .app--vscode-layout .detail__title {
      font-size: 13px;
      padding: 1px 4px;
      margin-left: -4px;
      gap: 6px;
    }
    .app--vscode-layout .detail__project {
      font-size: 11px;
      padding: 0 4px;
    }
    .app--vscode-layout .detail__meta {
      display: none;
    }
    /* Hide the secondary command deck and the pane-toggle bar inside the
       detail view. Their controls land in the status bar (status-bar
       component) and the per-pane "i" Meta toggle. The user can still
       start/stop runs from the chat composer. */
    .app--vscode-layout .detail app-command-deck,
    .app--vscode-layout .detail app-pane-toggle-bar,
    .app--vscode-layout .detail__panes-toolbar {
      display: none;
    }
    /* Pane chrome density: VS Code-style 6/12 padding, 12 px title. */
    .app--vscode-layout .pane {
      border-radius: 4px;
      border-color: rgba(255,255,255,0.04);
    }
    .app--vscode-layout .pane__header {
      padding: 4px 10px;
      min-height: 28px;
    }
    .app--vscode-layout .pane__title {
      font-size: 12px;
      letter-spacing: 0;
    }
    .app--vscode-layout .pane__title-icon {
      font-size: 12px;
    }
    /* When the meta-pane is closed, hide the verbose telemetry chips on
       the protocol header. The "i" toggle inside the protocol pane can
       reveal them again without forcing the user to read them by default. */
    .app--vscode-layout:not(.app--vscode-meta-open) .pane__telemetry,
    .app--vscode-layout:not(.app--vscode-meta-open) .pane__session-chip,
    .app--vscode-layout:not(.app--vscode-meta-open) .pane__watchdog {
      display: none;
    }
    .app--vscode-layout .pane__body {
      padding: 0;
    }
    /* Inspector tabs — tighten without changing structure. */
    .app--vscode-layout .inspector__header {
      padding: 0;
    }
    .app--vscode-layout .inspector__tabs {
      gap: 0;
    }

    /*
     * Kanban board design spec V1 (flag: Frontend:KanbanDesignSpecV1, default off)
     * Spec: docs/mockups/kanban-board-design/. Slice 1 lands the locked
     * grid template, the lane spacing rhythm, and the card sizing rules.
     * Off keeps the legacy flex layout untouched.
     */
    .app--kanban-spec-v1 .dashboard {
      display: grid;
      /* repeat(N, minmax(220px, 1fr)) - N is hard-coded at 7 for the
         current ADR-0025 lane vocabulary. Slice 4 reads N from the
         visible-lane count when failed-pickup gains a body. */
      grid-template-columns: repeat(7, minmax(220px, 1fr));
      gap: 4px;
      padding: 16px;
      background: var(--surface-2, #1e1e2e);
      align-items: stretch;
      width: 100%;
      max-width: none;
    }
    .app--kanban-spec-v1 .lane-group {
      display: contents;
      background: transparent;
      border: 0;
      padding: 0;
    }
    .app--kanban-spec-v1 .lane-group__head {
      display: none;
    }
    .app--kanban-spec-v1 .lane-group__lanes {
      display: contents;
      gap: 0;
    }
    .app--kanban-spec-v1 .column {
      background: var(--surface-1, #181825);
      border: 1px solid rgba(255, 255, 255, 0.06);
      border-radius: 6px;
      padding: 4px 8px 8px;
      min-width: 0;
      flex: initial;
      gap: 8px;
    }
    .app--kanban-spec-v1 .column__header {
      height: 36px;
      padding-bottom: 0;
      gap: 8px;
    }
    .app--kanban-spec-v1 .column__title {
      font-size: 13px;
    }
    .app--kanban-spec-v1 .column__count {
      font-size: 12px;
    }
    .app--kanban-spec-v1 .column-rail {
      width: 48px;
      min-width: 48px;
      flex: 0 0 48px;
      border-radius: 6px;
      padding: 8px 4px;
    }
  `]
})
export class App implements OnInit {
  readonly selectedJob = signal<JobDetail | null>(null);
  /** Transient banner shown by the triage panel auto-advance flow ("Lane
   *  cleared", "Job was moved externally; advancing"). Auto-cleared after
   *  ~3 s. Lives on the App so it survives the panel close/reopen during
   *  auto-advance. */
  readonly triageToast = signal<string | null>(null);
  private triageToastTimer: ReturnType<typeof setTimeout> | null = null;

  @ViewChild('jobDetail') private jobDetailRef?: JobDetailComponent;
  @ViewChild('orchConfigPanel') private orchConfigPanelRef?: OrchestratorConfigPanelComponent;
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
  readonly showCreate = signal(false);
  /**
   * When non-null, names the project whose orchestrator feed is currently
   * open as an overlay. The toolbar button toggles this for the active
   * project; the overlay closes on backdrop click.
   */
  readonly orchFeedProject = signal<string | null>(null);
  /** When non-null, names the project whose detail panel is open. */
  readonly projectDetailName = signal<string | null>(null);
  /**
   * Project page shell state. When `projectShellName` is non-null, the
   * project-shell overlay renders for that project and the URL hash is
   * `#/projects/<slug>` (or `#/projects/<slug>/<rail-key>`). The active
   * rail item drives which placeholder panel is shown. Slice 2 of the
   * quality-system mockup; real per-panel content lands later.
   */
  readonly projectShellName = signal<string | null>(null);
  readonly projectShellRail = signal<ProjectRailKey>(DEFAULT_PROJECT_RAIL_KEY);
  private readonly projectShellHashPrefix = '#/projects/';
  /**
   * When non-null, the (project, reportId) pair whose Analysis Reports
   * drill-down overlay is open. Stacked above the project-detail overlay so
   * the user can return to the list with a single click without losing
   * context.
   */
  readonly analysisReportFocus = signal<{ project: string; reportId: string } | null>(null);
  /** Workspace token timeline overlay. Triggered from the usage hover panel
   *  and from the deep-link `#/workspace/tokens` so it can be opened from
   *  another tab or a bookmark. */
  readonly workspaceTokensOpen = signal<boolean>(false);
  private readonly workspaceTokensHash = '#/workspace/tokens';
  /** Workspace visual evidence reel overlay. Triggered from the status
   *  bar entry and from the deep-link `#/workspace/screenshots`. */
  readonly workspaceScreenshotsOpen = signal<boolean>(false);
  private readonly workspaceScreenshotsHash = '#/workspace/screenshots';
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
   * Cycle 9 / ADR-0034: lane and container collapse state lives in
   * LaneCollapseService (features/board/state/lane-collapse.service.ts).
   * The shell exposes the same `collapsedLanes` / `collapsedContainers`
   * / `focusedContainer` signal references so existing template
   * bindings and computeds keep working unchanged. Methods further
   * down delegate to the service.
   */
  private readonly laneCollapse = inject(LaneCollapseService);
  readonly collapsedLanes = this.laneCollapse.collapsedLanes;
  readonly collapsedContainers = this.laneCollapse.collapsedContainers;
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
  readonly showUpdateStable = signal(false);
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
    // user has collapsed Active (or focus-expanded another container),
    // the lane element is not in the DOM and a scroll target would be
    // silently missing. Reset focus and expand Active before we look.
    if (this.focusedContainer() !== null && this.focusedContainer() !== 'active') {
      this.resetContainers();
    } else if (this.isContainerCollapsed('active')) {
      this.toggleContainerCollapse('active');
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

  /**
   * Peers in the same on-disk lane as the currently selected job, in the
   * existing kanban sort order. Drives the triage panel's counter and
   * `j` / `k` navigation. The mapping is keyed by the filesystem state on
   * `info.state`; virtual sub-lanes (e.g. `2-ready-intake`) merge back into
   * their parent because they share the same disk lane.
   */
  readonly triageLanePeers = computed<JobInfo[]>(() => {
    const sel = this.selectedJob();
    if (!sel) return [];
    const g = this.jobService.grouped();
    switch (sel.info.state) {
      case '0-backlog':              return g.backlog ?? [];
      case '1-preparation':          return g.preparation ?? [];
      case '1a-orchestrator-prep':   return g.orchestratorPrep ?? [];
      case '1b-needs-human-review':  return g.needsHumanReview ?? [];
      case '2-ready':                return g.ready ?? [];
      case '3-progress':             return g.progress ?? [];
      case '3a-failed-pickup':       return g.failedPickup ?? [];
      case '4-auto-review':          return g.autoReview ?? [];
      case '5-human-review':         return g.humanReview ?? [];
      case '6-completed':            return g.completed ?? [];
      case '7-archive':              return g.archive ?? [];
      default:                       return [];
    }
  });

  newTitle = '';
  newWatchPath = '';
  newAgent = 'copilot';
  newPrompt = '';
  newTargetState = '0-backlog';
  /** Backlog-lane spec: structural classification of the new task. */
  newTaskType = 'chore';
  /** Backlog-lane spec: tag ids attached on create. */
  newTags: string[] = [];
  newCliType: CliType = readDefaultCliPref();
  newModel = readDefaultModelPref(readDefaultCliPref());
  newAttachments: PendingAttachment[] = [];

  readonly cliTypes = CLI_TYPES;
  readonly availableModels = signal<CliModelInfo[]>([]);

  createDialogTitle(): string {
    switch (this.newTargetState) {
      case '2-ready': return 'Add Task to Ready';
      case '1-preparation': return 'Add Task to Preparation';
      case '0-backlog': return 'Add Task to Backlog';
      default: return 'Add Task';
    }
  }

  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }

  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }

  onCreateCliTypeChange(t: CliType) {
    if (this.newCliType === t) return;
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
    this.loadCreateModels(t);
  }

  /**
   * Status bar changed the default CLI for new tasks. Pre-fill the create
   * dialog so the next ＋ Add Task lands on the user's pick without making
   * them re-pick inside the dialog.
   */
  onDefaultCliChange(t: CliType): void {
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
  }

  onDefaultModelChange(ev: { cliType: CliType; model: string }): void {
    if (ev.cliType === this.newCliType) {
      this.newModel = ev.model;
    }
  }

  private loadCreateModels(cliType: CliType) {
    this.jobService.getCliModelCatalog(cliType).subscribe({
      next: (catalog) => {
        const models = catalog.models ?? [];
        this.availableModels.set(models);
        if (!this.newModel) {
          const def = models.find(m => m.isDefault);
          if (def) this.newModel = def.id;
        }
      },
      error: () => this.availableModels.set([])
    });
  }

  canAddTaskToGroup(state: string): boolean {
    return state === '0-backlog' || state === '1-preparation' || state === '2-ready';
  }

  readonly devToolsFlags = computed(() => this.devTools.flags());

  onPickUpdateStable(): void {
    this.devToolsMenuOpen.set(false);
    const ok = window.confirm(
      'Update Stable will:\n\n' +
      '  • stop the stable backend and frontend\n' +
      '  • git pull origin/main\n' +
      '  • npm install if needed\n' +
      '  • start stable again\n\n' +
      'If you trigger this from the stable instance itself, this page will ' +
      'lose the live console mid-run and you must reload after ~30 seconds.\n\n' +
      'Continue?'
    );
    if (ok) this.showUpdateStable.set(true);
  }

  onPickDeleteE2E(): void {
    this.devToolsMenuOpen.set(false);
    this.showE2ECleanup.set(true);
  }

  onPickOrchestratorConfig(): void {
    this.devToolsMenuOpen.set(false);
    void this.orchConfigPanelRef?.openPanel();
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
        (currentExecution?.processId ?? null) !== (latestExecution?.processId ?? null) ||
        (currentExecution?.exitCode ?? null) !== (latestExecution?.exitCode ?? null) ||
        (currentExecution?.durationSeconds ?? null) !== (latestExecution?.durationSeconds ?? null);

      if (selected.info.state === latest.state && !executionChanged) {
        return;
      }

      untracked(() => {
        this.jobService.getDetail(latest.id, latest.watchPath).subscribe({
          next: (detail) => this.selectedJob.set(detail),
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
      const lane = this.triageLaneState;
      if (!sel || !lane) return;
      if (sel.info.state === lane) return;
      if (this.jobDetailRef?.triageActingId() != null) return;
      const peers = untracked(() => this.triageLanePeers());
      // The job no longer matches the lane it was being triaged in; advance.
      untracked(() => this.advanceToNextInLane(lane, sel.info.jobKey, peers, /*external*/ true));
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
        if (entries.length > 0) this.newWatchPath = entries[0].path;
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
    this.restoreDetailFromUrl();

    // Deep-link: open the workspace token timeline when the URL already
    // points at it, and keep the overlay in sync as the hash changes.
    const applyHash = () => {
      const open = window.location.hash === this.workspaceTokensHash;
      if (open !== this.workspaceTokensOpen()) this.workspaceTokensOpen.set(open);
      const screenshotsOpen = window.location.hash === this.workspaceScreenshotsHash;
      if (screenshotsOpen !== this.workspaceScreenshotsOpen()) {
        this.workspaceScreenshotsOpen.set(screenshotsOpen);
      }
      this.applyProjectShellHash();
    };
    applyHash();
    this.hashListener = applyHash;
    window.addEventListener('hashchange', this.hashListener);

    // Keyboard shortcuts for kanban container focus-expand: 1/2/3 focus
    // the corresponding container, 0 resets all. Suppressed while the
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
      if (ev.key === '0') { this.resetContainers(); ev.preventDefault(); return; }
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

  private restoreDetailFromUrl() {
    const params = new URLSearchParams(window.location.search);
    const jobId = params.get('job');
    const watchPath = params.get('watchPath');
    if (jobId && watchPath) {
      this.jobService.getDetail(jobId, watchPath).subscribe({
        next: (detail) => this.selectedJob.set(detail),
        error: () => history.replaceState(null, '', window.location.pathname),
      });
    }
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: JobInfo) {
    history.replaceState(null, '', `?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
    // Anchor the triage lane to the lane the panel is opening in. Walking
    // peers and detecting external moves both key off this.
    this.triageLaneState = job.state;
    // Use a request token to discard responses that arrive after the user
    // has already closed the panel or opened a different job. Without this
    // the late `getDetail` reply re-sets `selectedJob` and the panel
    // pops back open — visible as a "j to advance, Esc fails to close"
    // race in the triage flow.
    const token = ++this.openDetailToken;
    this.jobService.getDetail(job.id, job.watchPath).subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        this.selectedJob.set(detail);
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        history.replaceState(null, '', window.location.pathname);
        this.errorDialog.show(err, {
          title: 'Failed to load task details',
          fallbackMessage: 'Failed to load task details',
          source: `Task ${job.id}`
        });
      }
    });
  }
  /** Monotonic token guarding the latest `openDetail` request; bumped on
   *  every open/close so a late HTTP reply for a stale job is dropped. */
  private openDetailToken = 0;

  isSelectedJob(job: JobInfo): boolean {
    return this.selectedJob()?.info.jobKey === job.jobKey;
  }

  closeDetail() {
    // Bump the token so any in-flight `openDetail` reply (e.g. the user
    // pressed `j` then immediately Esc) drops its `selectedJob.set` and
    // the panel does not pop back open after we close it.
    this.openDetailToken++;
    this.selectedJob.set(null);
    this.triageLaneState = null;
    history.replaceState(null, '', window.location.pathname);
  }

  /** Triage panel: lane-specific move action. Same path as a drag-and-drop
   *  move (optimistic paint + persist + revert-on-error), but on success we
   *  also auto-advance to the next peer in the lane the user was triaging. */
  onTriageMove(info: JobInfo, ev: { targetState: string; actionId: string }) {
    const lane = this.triageLaneState ?? info.state;
    const peers = this.triageLanePeers();
    const snapshot = this.jobService.applyOptimisticMove(info.id, info.watchPath, ev.targetState);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJob(info.id, ev.targetState, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.advanceToNextInLane(lane, info.jobKey, peers);
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (snapshot) this.jobService.revertOptimisticMove(snapshot);
        this.jobDetailRef?.clearTriageActing();
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${info.id}`
        });
      }
    });
  }

  /** Triage "Move to top" (only on 2-ready). Stays in lane; clear acting. */
  onTriageMoveToTop(info: JobInfo, _ev: { actionId: string }) {
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJobToTop(info.id, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.jobDetailRef?.clearTriageActing();
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        this.jobDetailRef?.clearTriageActing();
        this.errorDialog.show(err, {
          title: 'Failed to move task to top',
          fallbackMessage: 'Failed to move task to the top of the Ready queue',
          source: `Task ${info.id}`
        });
      }
    });
  }

  /** Triage "Delete". Confirm-on-first-click already happened in the panel,
   *  but we still surface the standard system confirm to match the menu's
   *  delete flow (so the user does not lose the safety net by accident). */
  onTriageDelete(info: JobInfo, _ev: { actionId: string }) {
    const lane = this.triageLaneState ?? info.state;
    const peers = this.triageLanePeers();
    const label = info.title || info.id;
    const message =
      `Delete this task?\n\n"${label}"\n\nThis removes the job folder and all its files (prompt, logs, results). Do you really want this?`;
    if (typeof window !== 'undefined' && !window.confirm(message)) {
      this.jobDetailRef?.clearTriageActing();
      return;
    }
    this.jobService.deleteJob(info.id, info.watchPath).subscribe({
      next: () => {
        this.refresh();
        this.advanceToNextInLane(lane, info.jobKey, peers);
      },
      error: (err) => {
        this.jobDetailRef?.clearTriageActing();
        this.errorDialog.show(err, {
          title: 'Failed to delete task',
          fallbackMessage: 'Failed to delete task',
          source: `Task ${info.id}`
        });
      }
    });
  }

  /** Triage "Run now": kick the start path then leave the panel on the same
   *  job (it will transition to 3-progress on its own). */
  onTriageStart(info: JobInfo, _ev: { actionId: string }) {
    this.jobService.startJob(info.id, info.watchPath).subscribe({
      next: () => this.jobDetailRef?.clearTriageActing(),
      error: (err) => {
        this.jobDetailRef?.clearTriageActing();
        this.errorDialog.show(err, {
          title: 'Failed to start task',
          fallbackMessage: 'Failed to start task',
          source: `Task ${info.id}`
        });
      }
    });
  }

  /** j / ↓: walk to the next peer in the current lane. */
  onTriageNext(info: JobInfo) {
    const peers = this.triageLanePeers();
    if (peers.length === 0) return;
    const idx = peers.findIndex(p => p.jobKey === info.jobKey);
    const nextIdx = idx < 0 ? 0 : Math.min(peers.length - 1, idx + 1);
    if (nextIdx === idx) return;
    this.openDetail(peers[nextIdx]);
  }

  /** k / ↑: walk to the previous peer in the current lane. */
  onTriagePrev(info: JobInfo) {
    const peers = this.triageLanePeers();
    if (peers.length === 0) return;
    const idx = peers.findIndex(p => p.jobKey === info.jobKey);
    const prevIdx = idx < 0 ? 0 : Math.max(0, idx - 1);
    if (prevIdx === idx) return;
    this.openDetail(peers[prevIdx]);
  }

  /**
   * After a triage decision (or external move), find the next peer in the
   * lane the user was triaging in, excluding the job that just left. If the
   * lane is empty, close the panel and toast.
   *
   * `external` flips the toast wording so the user can tell whether the
   * advance was their click or someone else's reshuffle.
   */
  private advanceToNextInLane(lane: string, departingJobKey: string, peersBefore: JobInfo[], external = false): void {
    this.jobDetailRef?.clearTriageActing();
    // Compute the candidate from the snapshot of peers we had before the
    // mutation: optimistic-persist may have already filtered out the moving
    // job, but we want the next peer that was after it in the original list.
    const idx = peersBefore.findIndex(p => p.jobKey === departingJobKey);
    let next: JobInfo | null = null;
    if (idx >= 0) {
      // Try the entry directly after the departing job; fall back to the
      // entry before it if it was the tail.
      next = peersBefore[idx + 1] ?? peersBefore[idx - 1] ?? null;
    } else if (peersBefore.length > 0) {
      next = peersBefore[0];
    }
    // Filter out the departing job itself (the snapshot may include it; we
    // also want to skip jobs that have since been moved out of the lane).
    const live = this.triageLanePeers().filter(p => p.jobKey !== departingJobKey && p.state === lane);
    const candidate = (next && live.find(p => p.jobKey === next!.jobKey)) ?? live[0] ?? null;
    if (candidate) {
      // Re-anchor lane to the new job's state (same lane unless poll drift)
      this.triageLaneState = candidate.state;
      const token = ++this.openDetailToken;
      this.jobService.getDetail(candidate.id, candidate.watchPath).subscribe({
        next: (detail) => {
          if (token !== this.openDetailToken) return;
          history.replaceState(null, '', `?job=${encodeURIComponent(candidate.id)}&watchPath=${encodeURIComponent(candidate.watchPath)}`);
          this.selectedJob.set(detail);
        },
        error: () => { /* leave panel on the previous job; the parent effect will reconcile */ }
      });
      if (external) this.showTriageToast('Job was moved externally; advancing.');
      return;
    }
    // Lane cleared — close the panel and toast.
    this.closeDetail();
    this.showTriageToast('Lane cleared.');
  }

  /** Show a transient triage banner; auto-clears after 3 s. */
  private showTriageToast(msg: string): void {
    if (this.triageToastTimer) clearTimeout(this.triageToastTimer);
    this.triageToast.set(msg);
    this.triageToastTimer = setTimeout(() => {
      this.triageToast.set(null);
      this.triageToastTimer = null;
    }, 3000);
  }

  onCompleteAndNextReview() {
    const currentJobKey = this.selectedJob()?.info.jobKey;
    const reviewJobs = this.jobService.grouped().review.filter(j => j.jobKey !== currentJobKey);
    this.refresh();
    if (reviewJobs.length > 0) {
      this.openDetail(reviewJobs[0]);
    } else {
      this.closeDetail();
    }
  }

  onJobDrop(event: { jobId: string; watchPath: string; targetState: string }) {
    // Optimistic move: paint the new lane immediately, let the backend
    // catch up. While the POST is in flight, silent polls are suppressed
    // so a stale /api/jobs/grouped response can't repaint the old lane.
    // On failure, revert the local snapshot and surface the error.
    // Virtual lanes inside the same filesystem state (e.g. the intake
    // sub-lane that splits 2-ready into "Human Ready" and "Orch Intake")
    // map back to the real state for the backend move; the orchestrator
    // intake loop is the only producer of the lane-defining `phase`
    // field, so a manual drag never has to write phase from the UI.
    if (event.targetState === '2-ready-intake') event = { ...event, targetState: '2-ready' };
    // Same-state drops (drag onto a sibling card in the same lane) are a
    // no-op: the column-level drop handler already filters the common path,
    // this is defense in depth so a stray emit cannot trigger a wasted
    // backend round-trip or a vanish-and-recover repaint.
    const moving = this.jobService.jobs().find(j => j.id === event.jobId && j.watchPath === event.watchPath);
    if (moving && moving.state === event.targetState) return;
    const snapshot = this.jobService.applyOptimisticMove(event.jobId, event.watchPath, event.targetState);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJob(event.jobId, event.targetState, event.watchPath).subscribe({
      next: () => this.jobService.endOptimisticPersist(),
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (snapshot) this.jobService.revertOptimisticMove(snapshot);
        this.jobService.error.set(err.message || 'Failed to move job');
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${event.jobId}`
        });
      },
    });
  }

  onJobReorder(event: { state: string; jobs: { jobId: string; watchPath: string }[] }) {
    // Optimistic reorder. The lane updates synchronously; in-flight
    // POST tracking + a short grace window after the response keep the
    // user-visible order stable while the backend rewrites job.json.
    const before = this.jobService.applyOptimisticReorder(event.state, event.jobs);
    this.jobService.beginOptimisticPersist();
    this.jobService.reorderJobs(event.jobs).subscribe({
      next: () => this.jobService.endOptimisticPersist(),
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (before) this.jobService.revertOptimisticReorder(event.state, before);
        this.jobService.error.set(err.message || 'Failed to reorder');
        this.errorDialog.show(err, {
          title: 'Failed to reorder tasks',
          fallbackMessage: 'Failed to reorder tasks',
          source: `Column ${event.state}`
        });
      },
    });
  }

  onDeleteFromBoard(job: JobInfo) {
    this.confirmAndDeleteJob(job, false);
  }

  onDeleteFromDetail(info: JobInfo) {
    this.confirmAndDeleteJob(info, true);
  }

  /**
   * Lane-dropdown move from the detail view. Mirrors the drag-and-drop path
   * (`onJobDrop`) so the board repaints optimistically while the POST is
   * in flight, then re-fetches the open detail so the dropdown reflects the
   * new lane. The detail-view's local "changing" flag is cleared by the
   * detail component's effect when the new `state` arrives.
   */
  onStateChangeFromDetail(info: JobInfo, targetState: string) {
    if (!targetState || targetState === info.state) return;
    const snapshot = this.jobService.applyOptimisticMove(info.id, info.watchPath, targetState);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJob(info.id, targetState, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.jobService.getDetail(info.id, info.watchPath).subscribe({
          next: (detail) => this.selectedJob.set(detail),
          error: () => { /* polling will reconcile */ }
        });
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (snapshot) this.jobService.revertOptimisticMove(snapshot);
        this.jobService.error.set(err.message || 'Failed to move job');
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${info.id}`
        });
      }
    });
  }

  private confirmAndDeleteJob(job: JobInfo, closeDetailOnSuccess: boolean) {
    const label = job.title || job.id;
    const message =
      `Delete this task?\n\n"${label}"\n\nThis removes the job folder and all its files (prompt, logs, results). Do you really want this?`;
    if (typeof window === 'undefined' || !window.confirm(message)) return;

    this.jobService.deleteJob(job.id, job.watchPath).subscribe({
      next: () => {
        if (closeDetailOnSuccess) this.closeDetail();
        this.refresh();
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to delete task',
          fallbackMessage: 'Failed to delete task',
          source: `Task ${job.id}`
        });
        this.refresh();
      }
    });
  }

  onArchiveAll() {
    const completed = this.filteredGrouped().completed;
    if (completed.length === 0) return;
    const moves = completed.map(job => this.jobService.moveJob(job.id, '7-archive', job.watchPath));
    forkJoin(moves).subscribe({
      next: () => this.refresh(),
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to archive tasks',
          fallbackMessage: 'One or more tasks could not be moved to Archive',
          source: 'Archive all'
        });
        this.refresh();
      }
    });
  }

  openCreate(targetState?: string) {
    this.newTargetState = targetState === '2-ready' ? '2-ready' : '1-preparation';
    this.newWatchPath = this.pickCreateWatchPath();
    this.loadCreateModels(this.newCliType);
    this.showCreate.set(true);
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
    if (this.projectDetailName()) return false;
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
    if (this.orchFeedProject() != null) {
      this.closeOrchFeed();
      return;
    }
    const project = this.pickOrchFeedProject();
    if (project) this.orchFeedProject.set(project);
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
    const token = ++this.openDetailToken;
    this.jobService.getDetail(event.jobId, event.watchPath).subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        this.selectedJob.set(detail);
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
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
    const watchEntry = this.watchPaths().find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = '1-preparation';
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.prefill;
    this.newTitle = `Security follow-up (${event.projectName})`;
    this.loadCreateModels(this.newCliType);
    this.showCreate.set(true);
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
    const watchEntry = this.watchPaths().find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = '1-preparation';
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.prefill;
    this.newTitle = event.title;
    this.loadCreateModels(this.newCliType);
    this.showCreate.set(true);
  }

  /** Refresh the kanban after a UX/UI design action was queued so the new job appears. */
  onUxuiActionQueued(_event: { projectName: string; action: string; jobId: string }): void {
    this.refresh();
  }

  onCreateTaskFromOrchestratorDraft(event: { projectName: string; promptText: string }): void {
    const watchEntry = this.watchPaths().find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = '1-preparation';
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.promptText;
    this.newTitle = deriveDraftTitle(event.promptText);
    this.loadCreateModels(this.newCliType);
    this.showCreate.set(true);
  }

  private pickOrchFeedProject(): string | null {
    const detail = this.selectedJob();
    if (detail?.info?.projectName) return detail.info.projectName;
    const active = [...this.activeProjects()];
    if (active.length > 0) return active[0];
    const watchPaths = this.watchPaths();
    return watchPaths.length > 0 ? watchPaths[0].name : null;
  }

  /** Open the project detail panel from a click on the project tab's ⚙ button. */
  openProjectDetail(name: string): void {
    this.projectDetailName.set(name);
  }

  closeProjectDetail(): void {
    this.projectDetailName.set(null);
  }

  /**
   * Open the project page shell (slice 2 of the quality-system mockup).
   * Pushes a hash so deep-links survive reload; the hash listener picks
   * the change up and updates `projectShellName` / `projectShellRail`.
   */
  openProjectShell(name: string, rail: ProjectRailKey = DEFAULT_PROJECT_RAIL_KEY): void {
    const slug = toProjectSlug(name);
    if (!slug) return;
    const target = `${this.projectShellHashPrefix}${slug}`
      + (rail !== DEFAULT_PROJECT_RAIL_KEY ? `/${rail}` : '');
    if (window.location.hash !== target) {
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
    // pushState doesn't fire hashchange; apply the resolved state directly.
    this.applyProjectShellHash();
  }

  closeProjectShell(): void {
    this.projectShellName.set(null);
    this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
    if (window.location.hash.startsWith(this.projectShellHashPrefix)) {
      try {
        history.pushState(null, '', window.location.pathname + window.location.search);
      } catch { /* ignore */ }
    }
  }

  onProjectShellRailChange(key: ProjectRailKey): void {
    const name = this.projectShellName();
    if (!name) return;
    this.projectShellRail.set(key);
    const slug = toProjectSlug(name);
    const target = `${this.projectShellHashPrefix}${slug}`
      + (key !== DEFAULT_PROJECT_RAIL_KEY ? `/${key}` : '');
    if (window.location.hash !== target) {
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  onOpenFeedFromShell(): void {
    const name = this.projectShellName();
    if (!name) return;
    // Stack the feed overlay over the shell (consistent with the project
    // detail overlay → feed overlay handoff). The shell stays mounted so
    // closing the feed returns to the same rail.
    this.orchFeedProject.set(name);
  }

  /**
   * Parse the URL hash and reconcile the project-shell signals with it.
   * Accepts `#/projects/<slug>` and `#/projects/<slug>/<rail-key>`. The
   * slug is mapped back to a project name by computing the slug for each
   * known watch path and matching. If watch paths haven't loaded yet, we
   * keep the signals untouched and re-run when they arrive.
   */
  private applyProjectShellHash(): void {
    const hash = window.location.hash;
    if (!hash.startsWith(this.projectShellHashPrefix)) {
      if (this.projectShellName() !== null) {
        this.projectShellName.set(null);
        this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
      }
      return;
    }
    const tail = hash.slice(this.projectShellHashPrefix.length);
    const [slugRaw, railRaw] = tail.split('/', 2);
    const slug = decodeURIComponent(slugRaw || '').toLowerCase();
    if (!slug) return;
    const entries = this.watchPaths();
    if (entries.length === 0) {
      // Hash arrived before /api/watch-paths returned. Leave the signals
      // alone; the watch-paths success handler re-runs this.
      return;
    }
    const match = entries.find(wp => toProjectSlug(wp.name) === slug);
    if (!match) {
      // Unknown slug — clear shell state but leave the URL alone so the
      // user can fix it manually rather than getting silently bounced.
      if (this.projectShellName() !== null) {
        this.projectShellName.set(null);
        this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
      }
      return;
    }
    const railKey: ProjectRailKey = isProjectRailKey(railRaw) ? railRaw : DEFAULT_PROJECT_RAIL_KEY;
    if (this.projectShellName() !== match.name) this.projectShellName.set(match.name);
    if (this.projectShellRail() !== railKey) this.projectShellRail.set(railKey);
  }

  /** Open the analysis-report drill-down overlay for one report. */
  openAnalysisReport(project: string, reportId: string): void {
    this.analysisReportFocus.set({ project, reportId });
  }

  closeAnalysisReport(): void {
    this.analysisReportFocus.set(null);
  }

  openWorkspaceTokens(): void {
    this.workspaceTokensOpen.set(true);
    if (window.location.hash !== this.workspaceTokensHash) {
      try { history.replaceState(null, '', window.location.pathname + window.location.search + this.workspaceTokensHash); } catch { /* ignore */ }
    }
  }

  closeWorkspaceTokens(): void {
    this.workspaceTokensOpen.set(false);
    if (window.location.hash === this.workspaceTokensHash) {
      try { history.replaceState(null, '', window.location.pathname + window.location.search); } catch { /* ignore */ }
    }
  }

  openWorkspaceScreenshots(): void {
    this.workspaceScreenshotsOpen.set(true);
    if (window.location.hash !== this.workspaceScreenshotsHash) {
      try { history.replaceState(null, '', window.location.pathname + window.location.search + this.workspaceScreenshotsHash); } catch { /* ignore */ }
    }
  }

  closeWorkspaceScreenshots(): void {
    this.workspaceScreenshotsOpen.set(false);
    if (window.location.hash === this.workspaceScreenshotsHash) {
      try { history.replaceState(null, '', window.location.pathname + window.location.search); } catch { /* ignore */ }
    }
  }

  toggleWorkspaceScreenshots(): void {
    if (this.workspaceScreenshotsOpen()) this.closeWorkspaceScreenshots();
    else this.openWorkspaceScreenshots();
  }

  /** CLI admin overlay: per-CLI usage caps + placeholder admin sections. */
  readonly cliAdminOpen = signal<boolean>(false);

  openCliAdmin(): void { this.cliAdminOpen.set(true); }
  closeCliAdmin(): void { this.cliAdminOpen.set(false); }
  toggleCliAdmin(): void {
    if (this.cliAdminOpen()) this.closeCliAdmin();
    else this.openCliAdmin();
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

  /**
   * "Open feed" button inside the project detail panel: close the detail
   * panel, open the orchestrator feed for the same project. Two stacked
   * overlays would be confusing; swap instead.
   */
  onOpenFeedFromDetail(name: string): void {
    this.projectDetailName.set(null);
    this.orchFeedProject.set(name);
  }

  private pickCreateWatchPath(): string {
    const paths = this.watchPaths();
    if (paths.length === 0) return '';
    const last = localStorage.getItem('lastCreateWatchPath');
    const isValid = (p: string | null) => !!p && paths.some(wp => wp.path === p);
    const active = this.activeProjects();
    const activePaths = paths.filter(wp => active.has(wp.name));

    if (activePaths.length === 1) {
      return activePaths[0].path;
    }
    if (activePaths.length > 1) {
      const lastInActive = activePaths.find(wp => wp.path === last);
      if (lastInActive) return lastInActive.path;
      return activePaths[0].path;
    }
    if (isValid(last)) return last as string;
    return paths[0].path;
  }

  cancelCreate() {
    this.showCreate.set(false);
    this.newTitle = '';
    this.newPrompt = '';
    this.newAgent = 'copilot';
    this.newTaskType = 'chore';
    this.newTags = [];
    this.newTargetState = '0-backlog';
    this.newCliType = readDefaultCliPref();
    this.newModel = readDefaultModelPref(this.newCliType);
    this.availableModels.set([]);
    for (const att of this.newAttachments) URL.revokeObjectURL(att.previewUrl);
    this.newAttachments = [];
  }

  submitCreate() {
    const attachments = this.newAttachments;
    const promptDraft = this.newPrompt.trim();
    const watchPath = this.newWatchPath;

    // When attachments are present we defer writing the prompt to the create
    // call (its `pending-attachment-…` placeholders are not yet resolvable),
    // upload each image against the new jobId, then PUT prompt.md with the
    // real `attachments/<file>` references.
    const initialPrompt = attachments.length > 0 ? undefined : (promptDraft || undefined);

    this.jobService.createJob({
      title: this.newTitle.trim(),
      watchPath,
      agent: this.newCliType,
      promptMarkdown: initialPrompt,
      targetState: this.newTargetState,
      cliType: this.newCliType,
      model: this.newModel.trim() || undefined,
      taskType: this.newTaskType,
      tags: this.newTags.length > 0 ? [...this.newTags] : undefined
    }).subscribe({
      next: (res) => {
        localStorage.setItem('lastCreateWatchPath', watchPath);
        if (attachments.length > 0) {
          void this.uploadCreateAttachments(res.id, watchPath, promptDraft, attachments);
        }
        this.cancelCreate();
        this.refresh();
      },
      error: (err) => {
        this.jobService.error.set(err.error || 'Failed to create job');
        this.errorDialog.show(err, {
          title: 'Failed to create task',
          fallbackMessage: 'Failed to create task',
          source: 'Task creation'
        });
      },
    });
  }

  private async uploadCreateAttachments(
    jobId: string,
    watchPath: string,
    promptDraft: string,
    attachments: PendingAttachment[]
  ): Promise<void> {
    let prompt = promptDraft;
    for (const att of attachments) {
      try {
        const form = new FormData();
        form.append('file', att.file, att.file.name || `${att.alt}.png`);
        const url = `/api/jobs/${encodeURIComponent(jobId)}/attachments`
          + (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
        const res = await fetch(url, { method: 'POST', body: form });
        if (!res.ok) {
          this.errorDialog.show(new Error(`Upload failed (${res.status}) for ${att.file.name || att.alt}`), {
            title: 'Attachment upload failed',
            fallbackMessage: 'Could not upload one of the pasted images.',
            source: `Task ${jobId}`
          });
          continue;
        }
        const payload = (await res.json()) as { fileName: string; relativePath: string };
        prompt = prompt.replace(
          new RegExp(`pending-attachment-${att.id}`, 'g'),
          payload.relativePath
        );
      } catch (err) {
        this.errorDialog.show(err as Error, {
          title: 'Attachment upload failed',
          fallbackMessage: 'Could not upload one of the pasted images.',
          source: `Task ${jobId}`
        });
      }
    }

    this.jobService.updateJobFile(jobId, 'prompt.md', prompt, watchPath).subscribe({
      next: () => this.refresh(),
      error: (err) => this.errorDialog.show(err, {
        title: 'Failed to save prompt',
        fallbackMessage: 'Attachments uploaded, but writing prompt.md failed.',
        source: `Task ${jobId}`
      })
    });
  }

  toggleProject(name: string) { this.boardFilters.toggleProject(name); }
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

  onFileSaved() {
    // Re-fetch detail to reflect changes, and refresh the board so updates
    // (e.g. renamed titles) propagate to the card and task-nav views immediately.
    const current = this.selectedJob();
    if (current) {
      this.jobService.getDetail(current.info.id, current.info.watchPath).subscribe({
        next: (detail) => this.selectedJob.set(detail),
      });
    }
    this.jobService.refresh(true);
  }

  onProjectChanged(targetWatchPath: string) {
    const current = this.selectedJob();
    this.closeDetail();
    this.jobService.refresh();
    if (current) {
      // Re-open detail after refresh
      setTimeout(() => {
        this.jobService.getDetail(current.info.id, targetWatchPath).subscribe({
          next: (detail) => this.selectedJob.set(detail),
          error: (err) => {
            this.errorDialog.show(err, {
              title: 'Task moved, but detail view could not be reopened',
              fallbackMessage: 'Task moved, but detail view could not be reopened automatically.',
              source: `Task ${current.info.id}`
            });
          }
        });
      }, 500);
    }
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

  // Cycle 9: collapse + focus methods delegate to LaneCollapseService.
  // The shell forwards the lane-id list so the service stays free of
  // the kanban catalogue shape; everything else is straight pass-through.

  toggleLaneCollapse(state: string): void { this.laneCollapse.toggleLaneCollapse(state); }
  isLaneCollapsed(state: string): boolean { return this.laneCollapse.isLaneCollapsed(state); }
  expandedLaneCount(group: { lanes: Array<{ state: string }> }): number {
    return this.laneCollapse.expandedLaneCount(group);
  }
  isContainerCollapsed(id: string): boolean { return this.laneCollapse.isContainerCollapsed(id); }
  toggleContainerCollapse(id: string): void { this.laneCollapse.toggleContainerCollapse(id); }
  isContainerFocused(id: string): boolean { return this.laneCollapse.isContainerFocused(id); }
  toggleContainerFocus(id: string): void {
    this.laneCollapse.toggleContainerFocus(id, this.laneGroups().map(g => g.id));
  }
  resetContainers(): void { this.laneCollapse.resetContainers(); }
  containerSummary(id: string): Array<{ state: string; icon: string; title: string; count: number }> {
    return this.laneCollapse.containerSummary(this.laneGroups().find(g => g.id === id));
  }

  // Cycle 9: UI-pref methods delegate to UiPreferencesService.
  setTaskNavCollapsed(collapsed: boolean): void { this.uiPrefs.setTaskNavCollapsed(collapsed); }
  toggleCompactCards(): void { this.uiPrefs.toggleCompactCards(); }
  startResize(event: MouseEvent): void { this.uiPrefs.startResize(event); }

}

function readDefaultCliPref(): CliType {
  const stored = localStorage.getItem('defaultCliType') as CliType | null;
  if (stored && (CLI_TYPES as string[]).includes(stored)) return stored;
  return 'copilot';
}

function readDefaultModelPref(cliType: CliType): string {
  return localStorage.getItem('defaultModel:' + cliType) ?? '';
}

/**
 * Best-effort task title from a Markdown reply: take the first non-empty
 * line, strip Markdown decoration, cap at 80 chars. Used by
 * `onCreateTaskFromOrchestratorDraft` so the user lands in the create
 * dialog with a placeholder title instead of an empty field.
 */
function deriveDraftTitle(text: string): string {
  if (!text) return '';
  for (const raw of text.split('\n')) {
    const line = raw.replace(/^#+\s*/, '').replace(/[*_`]/g, '').trim();
    if (line.length === 0) continue;
    return line.length > 80 ? line.slice(0, 77).trim() + '...' : line;
  }
  return '';
}
