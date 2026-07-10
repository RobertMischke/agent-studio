import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { TaskInfo, RegistryWorkspaceListItem, WatchPathEntry } from '../../models/task.model';
import { TaskService } from '../../services/task.service';
import { StudioIconComponent } from '../../components/studio-icon/studio-icon.component';
import { StudioSidebarHeaderComponent } from '../../components/studio-sidebar-header/studio-sidebar-header.component';
import { EmptyStateComponent } from '../../components/empty-state/empty-state.component';
import { StudioEmptyStateComponent } from './components/studio-empty-state/studio-empty-state.component';
import { SectionHeaderComponent } from '../../components/section-header/section-header.component';
import { CountBadgeComponent } from '../../components/count-badge/count-badge.component';
import { ListRowComponent } from '../../components/list-row/list-row.component';
import { SegmentedControlComponent, SegmentedOption } from '../../components/segmented-control/segmented-control.component';
import { ClientService } from '../../services/client.service';
import { FeatureFlagsService } from '../../services/feature-flags.service';
import { projectIdentity } from '../../services/project-identity.util';
import { TaskSelectionService } from '../task-detail';
import { ProjectDetailComponent } from '../project-detail';
import {
  PROJECT_RAIL_ITEMS,
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectRailKey,
  type ProjectRailItem,
} from '../project-detail/components/project-shell/project-shell.config';
import type { StudioIconName } from '../../components/studio-icon/studio-icon.component';
import { UiPreferencesService } from '../shell';
import { BoardFiltersService, flattenGrouped } from '../board';
import { UpdateClientService } from '../../services/update.service';
import { ConfirmDialogService } from '../../services/confirm-dialog.service';
import { NotificationService } from '../../services/notification.service';
import { copyTextToClipboard } from '../../services/clipboard.util';
import { WorkspaceManagerService, ProjectDragDropService, WorkspaceOverlaysService } from '../shell';
import { WorkspaceSettingsService } from '../shell/state/workspace-settings.service';
import { ProjectLookupService } from '../../services/project-lookup.service';
import { StudioActivityBarComponent, StudioActivityBarItem, StudioActivityPanelKey } from './components/studio-activity-bar/studio-activity-bar.component';
import { resolveActiveActivityKey } from './components/studio-activity-bar/studio-activity-bar.active-key';
import { ExplorerWorkspaceTreeComponent, type ExplorerProjectSurface } from './components/explorer-workspace-tree/explorer-workspace-tree.component';
import { deriveProjectPulseByName, type ProjectPulseState } from './studio-shell.pulse';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../../components/menu';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskStatusPopoverDirective } from '../../components/task-status-card';
import { buildProjectPickerItems, buildTabCtxMenuItems } from './studio-shell.menu-builders';
import { StudioTabStateService } from './services/studio-tab-state.service';
import { StudioPanelStateService } from './services/studio-panel-state.service';
import { ExplorerSectionsService } from './services/explorer-sections.service';
import { buildProjectSidebarRows, type ProjectSidebarRow } from './studio-shell.project-rows';
import { StudioTab, studioTabKey } from './studio-shell.types';
import { GlobalSearchComponent } from './components/global-search/global-search.component';

/** Canonicalise project storage paths so titlebar workspace lookup survives
 * slash style, trailing separator, and case differences. */
function normalizeStorage(path: string): string {
  return path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
}


/** Brand swatches per CLI — matches the status-bar glyph colours so the
 *  Sidebar CLI panel reads the same on first glance. */
function cliColorFor(cli: string): string {
  switch (cli) {
    case 'claude':  return '#d97757';
    case 'codex':   return '#569cd6';
    case 'gemini':  return '#c586c0';
    default:        return '#6e6e6e';
  }
}

const SETTINGS_WORKSPACES_COLLAPSED_KEY = 'atp.studio.settingsWorkspacesCollapsed';

/**
 * Top-level "Agent Software Studio" shell — the VS-Code-inspired chrome
 * that replaces the legacy single-pane layout. Behind the `vsCodeLayout`
 * feature flag for now; flip to default once the rest of the views
 * (Project Hub, full-screen diff, full-screen activity) are migrated.
 *
 * Owns the chrome (titlebar / activity bar / sidebar host / tab host /
 * status bar) and delegates state to the studio-shell services so child
 * panels and tabs can read it without prop drilling. Existing feature
 * components (`<app-job-column>`, `<app-job-detail>`, …) render inside
 * the tab area unchanged — this component is a wrapper, not a rewrite.
 */
@Component({
  selector: 'app-studio-shell',
  standalone: true,
  imports: [FormsModule, StudioIconComponent, StudioSidebarHeaderComponent, EmptyStateComponent, StudioEmptyStateComponent, SectionHeaderComponent, CountBadgeComponent, ListRowComponent, StudioActivityBarComponent, MenuComponent, TooltipDirective, TaskStatusPopoverDirective, ExplorerWorkspaceTreeComponent, SegmentedControlComponent, ProjectDetailComponent, GlobalSearchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './studio-shell.component.html',
  styleUrl: './studio-shell.component.scss',
})
export class StudioShellComponent {
  private readonly jobService = inject(TaskService);
  readonly clientService = inject(ClientService);

  /**
   * Backend-known project names (from the WatchPaths config). The
   * shell uses this so projects with zero working-set jobs still
   * appear in the picker / explorer. Defaults to [] so legacy hosts
   * that don't pass it keep their job-derived behaviour.
   */
  readonly knownProjectNames = input<readonly string[]>([]);

  /** Backend WatchPaths feeding the rename-stable `projectStorageByName` join
   *  (a row's storage == registry `storageLocation`, stable across rename). */
  readonly projectWatchPaths = input<readonly WatchPathEntry[]>([]);

  /**
   * Host-computed badge for the Filters activity-bar item. The host owns
   * the distinction between real filters and view/scope state, while this
   * shell owns the rail button that renders the count.
   */
  readonly filterBadgeCount = input<number | null>(null);

  /** Emitted after a delete so the host re-pulls WatchPaths + refreshes. */
  readonly projectDeleted = output<void>();

  private readonly featureFlags = inject(FeatureFlagsService);
  private readonly tabState = inject(StudioTabStateService);
  private readonly panelState = inject(StudioPanelStateService);
  private readonly jobSelection = inject(TaskSelectionService);
  readonly uiPrefs = inject(UiPreferencesService);
  readonly boardFilters = inject(BoardFiltersService);
  readonly updateClient = inject(UpdateClientService);
  readonly explorerSections = inject(ExplorerSectionsService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly notifications = inject(NotificationService);
  private readonly workspaceManager = inject(WorkspaceManagerService);
  private readonly workspaceOverlays = inject(WorkspaceOverlaysService);
  private readonly projectLookup = inject(ProjectLookupService);
  readonly wsSettings = inject(WorkspaceSettingsService);

  /** Tab list + active selection re-exposed for the template. */
  readonly tabs = this.tabState.tabs;
  readonly activeKey = this.tabState.activeKey;
  readonly activeTab = this.tabState.activeTab;
  readonly tabKey = studioTabKey;

  /** Sidebar panel state re-exposed for the template. */
  readonly activePanel = this.panelState.active;
  readonly sidebarVisible = this.panelState.visible;
  readonly sidebarWidth = this.panelState.sidebarWidth;
  readonly activityBarSide = this.panelState.activityBarSide;
  readonly chatRailOpen = this.panelState.chatRailOpen;
  readonly settingsWorkspacesCollapsed = signal(this.readSettingsWorkspacesCollapsed());
  readonly globalSearchOpen = signal(false);

  /** Settings segmented-control option sets (label/value pairs). */
  readonly themeOptions: readonly SegmentedOption<'dark' | 'light'>[] = [
    { value: 'dark', label: 'Dark', testid: 'settings-theme-dark' },
    { value: 'light', label: 'Light', testid: 'settings-theme-light' },
  ];
  readonly activityBarSideOptions: readonly SegmentedOption<'left' | 'right'>[] = [
    { value: 'left', label: 'Left', testid: 'settings-activitybar-left' },
    { value: 'right', label: 'Right', testid: 'settings-activitybar-right' },
  ];

  /**
   * Which Explorer-tree project rows are expanded (showing Board / Project
   * Hub / Activity sub-items). Persists across reloads so the user's
   * preferred tree shape survives an F5.
   */
  private readonly _expandedProjects = signal<Set<string>>(
    new Set(this.readExpandedProjects()),
  );
  readonly expandedProjects = this._expandedProjects.asReadonly();

  toggleProjectExpanded(name: string, event?: Event): void {
    event?.stopPropagation();
    this._expandedProjects.update(set => {
      const next = new Set(set);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      this.writeExpandedProjects(next);
      return next;
    });
  }

  private readExpandedProjects(): string[] {
    if (typeof window === 'undefined') return [];
    try {
      const raw = window.localStorage?.getItem('atp.studio.explorer.expanded');
      if (!raw) return [];
      const arr = JSON.parse(raw) as unknown;
      if (Array.isArray(arr)) return arr.filter((s): s is string => typeof s === 'string');
      return [];
    } catch {
      return [];
    }
  }

  private writeExpandedProjects(set: Set<string>): void {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage?.setItem('atp.studio.explorer.expanded', JSON.stringify([...set]));
    } catch {
      /* storage may be full / blocked */
    }
  }

  /**
   * Drives the "moon / sun" icon in the titlebar. Default is Light per the
   * reference design — a missing storage key counts as "use the default";
   * explicit user choice persists.
   */
  readonly theme = signal<'dark' | 'light'>(
    (localStorage.getItem('atp.studio.theme') as 'dark' | 'light' | null) ?? 'light',
  );

  /** Bubbles to app.ts so the parent can flip the orchestrator side
   *  sheet open without the shell needing a reference to it. */
  readonly chatToggle = output<void>();
  readonly addTaskRequested = output<void>();
  readonly openUsageSheet = output<void>();
  readonly openCliAdmin = output<void>();
  readonly openWorkspaceScreenshots = output<void>();
  readonly openOrchFeed = output<void>();
  readonly openOrchSettings = output<void>();
  /** Emits when the user toggles the auto-pickup mode for a project. */
  readonly toggleAuto = output<string>();

  /** Project picker dropdown open state. */
  readonly pickerOpen = signal(false);

  togglePickerMenu(ev: Event): void {
    ev.stopPropagation();
    this.pickerOpen.update(v => !v);
  }
  closePickerMenu(): void { this.pickerOpen.set(false); }

  /**
   * Centralised click handler for project-picker entries. `name === null`
   * means "All projects" (clears the active project filter); otherwise the
   * named project becomes the active board. `openHub` flag promotes the
   * click to a Project Hub open (double-click affordance).
   */
  pickProject(name: string | null, openHub = false): void {
    this.closePickerMenu();
    if (name === null) { this.openBoard('__all__'); return; }
    if (openHub) { this.openHub(name); return; }
    this.openBoard(name);
  }

  /** Closes the picker when the user clicks anywhere else in the document. */
  @HostListener('document:click')
  onDocumentClick(): void { this.closePickerMenu(); }

  /**
   * Active project for the picker — derived from the active tab / board.
   * `null` means the user is in "All projects" mode (workspace-wide).
   */
  readonly activeProjectName = computed<string | null>(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    if (tab.kind === 'board') return tab.projectName === '__all__' ? null : tab.projectName;
    if (tab.kind === 'epics') return tab.projectName;
    return this.currentProjectName();
  });

  readonly activeProjectInitial = computed<string>(() => {
    const name = this.activeProjectName();
    if (!name) return '';
    return projectIdentity(name).initial;
  });

  readonly activeProjectColor = computed<string>(() => {
    const name = this.activeProjectName();
    if (!name) return 'var(--studio-fg-muted)';
    return projectIdentity(name).color;
  });

  readonly activeProjectTotalJobs = computed<number>(() => {
    const name = this.activeProjectName();
    if (!name) return 0;
    return this.projectRows().find(r => r.name === name)?.totalJobs ?? 0;
  });

  activeProjectPickerLabel(): string {
    return this.activeProjectName() ?? 'All projects';
  }

  readonly totalProjectJobs = computed<number>(() =>
    this.projectRows().reduce((sum, r) => sum + r.totalJobs, 0)
  );

  /** Reactive map of project name → current runner mode. */
  autoModeFor(name: string): string {
    return this.jobService.runnerStatus().projects[name]?.mode ?? 'manual';
  }

  /** AGT-2031 — project name → auto-pickup pulse state for the Explorer tree's
   *  subtle activity indicator (derivation lives in studio-shell.pulse). */
  readonly projectPulseByName = computed<ReadonlyMap<string, ProjectPulseState>>(() =>
    deriveProjectPulseByName(this.jobService.runnerStatus().projects, this.projectRows()),
  );

  /** Short label for the auto-mode chip ("auto", "single", "paused", "manual"). */
  autoModeLabelFor(name: string): string {
    const mode = this.autoModeFor(name);
    switch (mode) {
      case 'auto-continuous': return 'Auto';
      case 'auto-single':     return 'Auto · 1';
      case 'paused':          return 'Paused';
      default:                return 'Manual';
    }
  }

  autoToggleTooltip(name: string): string {
    const mode = this.autoModeFor(name);
    if (mode === 'auto-continuous' || mode === 'auto-single') {
      return `Auto-pickup is on for ${name} — click to pause.`;
    }
    return `Auto-pickup is paused for ${name} — click to enable.`;
  }

  /** Reflects the theme onto the document root so the design tokens flip. */
  private readonly themeFx = effect(() => {
    const t = this.theme();
    document.documentElement.dataset['studioTheme'] = t;
    localStorage.setItem('atp.studio.theme', t);
  });

  /**
   * F47 / ADR-0042 — registry-backed workspace list rendered by the
   * Settings panel "Workspaces" section. F45b mutation surface lives
   * inline in this component: see `createRegistryWorkspace`,
   * `renameRegistryWorkspace`, `editRegistryWorkspaceColor`,
   * `moveRegistryWorkspace`, `deleteRegistryWorkspace`.
   */
  readonly registryWorkspaces = signal<readonly RegistryWorkspaceListItem[]>([]);
  readonly registryWorkspacesLoading = signal(false);
  readonly registryWorkspacesError = signal<string | null>(null);
  /** Workspace id currently waiting for a mutation response (disables its row buttons). */
  readonly registryWorkspaceBusyId = signal<string | null>(null);

  /**
   * Lazy-load the registry workspaces whenever a panel that renders them is
   * visible, then re-pull on every re-open so a mutation from another tab is
   * reflected without a full reload. Both the Settings panel (management) and
   * the Explorer panel (F46 two-level workspace tree) need the registry; the
   * Explorer is the default panel, so this also primes the tree on boot —
   * without it the tree would fall back to the single legacy "Workspace"
   * folder because `registryWorkspaces()` would stay empty.
   */
  private readonly loadRegistryWorkspacesFx = effect(() => {
    const panel = this.activePanel();
    const visible = this.sidebarVisible();
    if (!visible || (panel !== 'settings' && panel !== 'explorer')) return;
    this.reloadRegistryWorkspaces();
  });

  /** Reload the registry workspace list whenever the create-dialog or
   *  delete path bumps the counter via WorkspaceManagerService. */
  private readonly registryChangedFx = effect(() => {
    const rev = this.workspaceManager.registryChanged();
    if (rev === 0) return;
    untracked(() => this.reloadRegistryWorkspaces());
  });

  reloadRegistryWorkspaces(): void {
    this.registryWorkspacesLoading.set(true);
    this.registryWorkspacesError.set(null);
    this.jobService.getRegistryWorkspaces({ includeArchived: this.showArchivedProjects() }).subscribe({
      next: (ws) => {
        this.registryWorkspaces.set(ws ?? []);
        this.projectLookup.setWorkspaces(ws ?? []);
        this.registryWorkspacesLoading.set(false);
      },
      error: (err: unknown) => {
        this.registryWorkspacesError.set(this.errMsg(err));
        this.registryWorkspacesLoading.set(false);
      },
    });
  }

  /** F45b — prompt for a new workspace name and create it. */
  createRegistryWorkspace(): void {
    const name = window.prompt('New workspace name')?.trim();
    if (!name) return;
    this.jobService.createRegistryWorkspace(name).subscribe({
      next: () => this.reloadRegistryWorkspaces(),
      error: (err: unknown) => this.registryWorkspacesError.set(this.errMsg(err)),
    });
  }

  /**
   * F66 / ADR-0048 — click-to-edit rename. The settings service owns the
   * inline-edit state, validation, and PUT call; this shell method exists
   * only as the click handler on the workspace-name button so the template
   * binding does not have to know about the service.
   */
  renameRegistryWorkspace(ws: RegistryWorkspaceListItem): void {
    this.wsSettings.startRename(ws.id, ws.displayName);
  }

  /** ViewChild on the inline rename input — focus it the moment a row
   *  enters edit mode. Avoids needing an autofocus directive. */
  private readonly renameInputRef = viewChild<ElementRef<HTMLInputElement>>('renameInput');

  private readonly focusRenameInputFx = effect(() => {
    if (this.wsSettings.renamingId() === null) return;
    const el = this.renameInputRef()?.nativeElement;
    if (!el) return;
    queueMicrotask(() => { el.focus(); el.select(); });
  });

  /** F45b — prompt for an accent color hex string. Empty input clears the color. */
  editRegistryWorkspaceColor(ws: RegistryWorkspaceListItem): void {
    const input = window.prompt(
      `Workspace "${ws.displayName}" color (hex like #a78bfa, blank to clear)`,
      ws.color ?? '');
    if (input === null) return;
    const color = input.trim();
    const patch = color ? { color } : { clearColor: true };
    this.registryWorkspaceBusyId.set(ws.id);
    this.jobService.updateRegistryWorkspace(ws.id, patch).subscribe({
      next: () => { this.registryWorkspaceBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /** F45b — move a workspace one slot up or down. */
  moveRegistryWorkspace(ws: RegistryWorkspaceListItem, direction: -1 | 1): void {
    this.registryWorkspaceBusyId.set(ws.id);
    this.jobService.reorderRegistryWorkspace(ws.id, direction).subscribe({
      next: () => { this.registryWorkspaceBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /**
   * F66 — delete a workspace after a confirm dialog. Delete is only allowed
   * once the workspace is empty: per ADR-0048 the operator must move every
   * project out first (no auto-rehome). The button is disabled while projects
   * remain, and the backend rejects a populated delete with a 409; this guard
   * is the matching client-side defence.
   */
  deleteRegistryWorkspace(ws: RegistryWorkspaceListItem): void {
    if (!this.canDeleteWorkspace(ws)) return;
    const ok = window.confirm(`Delete workspace "${ws.displayName}"?`);
    if (!ok) return;
    this.registryWorkspaceBusyId.set(ws.id);
    this.jobService.deleteRegistryWorkspace(ws.id).subscribe({
      next: () => {
        this.registryWorkspaceBusyId.set(null);
        this.reloadRegistryWorkspaces();
      },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /** A workspace is deletable only when it is non-default and holds no
   *  projects; the operator moves projects out first (no auto-rehome). */
  canDeleteWorkspace(ws: RegistryWorkspaceListItem): boolean {
    return !ws.isDefault && ws.projects.length === 0;
  }

  /** Tooltip explaining the delete button's enabled/disabled reason:
   *  default workspace is never deletable, a populated one must be emptied
   *  first, an empty non-default one is ready to delete. */
  workspaceDeleteTooltip(ws: RegistryWorkspaceListItem): string {
    if (ws.isDefault) return 'Default workspace cannot be deleted';
    const count = ws.projects.length;
    if (count > 0) {
      return `Move all ${count} project${count === 1 ? '' : 's'} out of this workspace before it can be deleted.`;
    }
    return 'Delete this workspace';
  }

  /** Up arrow is disabled when the workspace is already at the top of the list. */
  canMoveWorkspaceUp(ws: RegistryWorkspaceListItem): boolean {
    const list = this.registryWorkspaces();
    return list.length > 0 && list[0].id !== ws.id;
  }

  /** Down arrow is disabled when the workspace is already at the bottom of the list. */
  canMoveWorkspaceDown(ws: RegistryWorkspaceListItem): boolean {
    const list = this.registryWorkspaces();
    return list.length > 0 && list[list.length - 1].id !== ws.id;
  }

  /** F45b — id of the project currently waiting for a mutation response. */
  readonly registryProjectBusyId = signal<string | null>(null);

  /** F45b — rename a project by prompt. */
  renameRegistryProject(projId: string, currentDisplayName: string): void {
    const name = window.prompt(`Rename project "${currentDisplayName}"`, currentDisplayName)?.trim();
    if (!name || name === currentDisplayName) return;
    this.runProjectPatch(projId, { displayName: name });
  }

  /** F45b — change the short code (2-6 chars A-Z 0-9). */
  editRegistryProjectShortCode(projId: string, currentShortCode: string): void {
    const code = window.prompt(
      `Project ${projId} short code (2-6 chars, A-Z and 0-9)`, currentShortCode)?.trim();
    if (!code || code.toUpperCase() === currentShortCode) return;
    this.runProjectPatch(projId, { shortCode: code });
  }

  /** F45b — set or clear the project color. */
  editRegistryProjectColor(projId: string, currentColor: string | null): void {
    const input = window.prompt(
      `Project ${projId} color (hex like #a78bfa, blank to clear)`, currentColor ?? '');
    if (input === null) return;
    const color = input.trim();
    this.runProjectPatch(projId, color ? { color } : { clearColor: true });
  }

  /** F45b — reassign project to a different workspace via dropdown prompt. */
  changeRegistryProjectWorkspace(projId: string, currentWorkspaceId: string): void {
    const options = this.registryWorkspaces();
    if (options.length < 2) {
      window.alert('Create another workspace first via "+ New workspace" above.');
      return;
    }
    const list = options
      .map(w => `  ${w.id} — ${w.displayName}${w.id === currentWorkspaceId ? ' (current)' : ''}`)
      .join('\n');
    const choice = window.prompt(
      `Move project ${projId} to which workspace? Enter id:\n\n${list}`, currentWorkspaceId)?.trim();
    if (!choice || choice === currentWorkspaceId) return;
    this.runProjectPatch(projId, { workspaceId: choice });
  }

  /** F45b — archive (or un-archive) a project. */
  toggleRegistryProjectArchived(projId: string, archived: boolean): void {
    const verb = archived ? 'Un-archive' : 'Archive';
    const ok = window.confirm(`${verb} project ${projId}? Archived projects are hidden from the tree by default.`);
    if (!ok) return;
    this.runProjectPatch(projId, { archived: !archived });
  }

  private runProjectPatch(projId: string, patch: {
    displayName?: string;
    shortCode?: string;
    color?: string | null;
    clearColor?: boolean;
    workspaceId?: string;
    archived?: boolean;
  }): void {
    this.registryProjectBusyId.set(projId);
    this.jobService.updateRegistryProject(projId, patch).subscribe({
      next: () => { this.registryProjectBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryProjectBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /** Toggle whether archived projects are shown in the Settings panel. Off by default. */
  readonly showArchivedProjects = signal(false);
  toggleShowArchivedProjects(): void {
    this.showArchivedProjects.update(v => !v);
    this.reloadRegistryWorkspaces();
  }

  private errMsg(err: unknown): string {
    if (err && typeof err === 'object' && 'error' in err) {
      const inner = (err as { error?: unknown }).error;
      if (inner && typeof inner === 'object' && 'error' in inner)
        return String((inner as { error?: unknown }).error);
      if (typeof inner === 'string') return inner;
    }
    if (err instanceof Error) return err.message;
    return 'Request failed';
  }

  /**
   * Track which active-project name we've already auto-expanded for, so
   * the auto-expand effect only fires when the active project CHANGES —
   * not when the user manually collapses it. Without this guard the
   * effect re-runs on every `_expandedProjects` mutation, instantly
   * re-expanding the project the user just collapsed.
   */
  private lastAutoExpandedActive: string | null = null;

  /**
   * Auto-expand the active project in the Explorer tree so the lane
   * children (backlog / active / human review / Project Hub / archive)
   * are visible the moment the user opens a board or task. Matches the
   * agent-orchestrator.zip mockup, which always shows the active project
   * expanded.
   *
   * Only acts when the active project name CHANGES — never on
   * `_expandedProjects` mutations, so the user's chevron-collapse is
   * preserved. We read `_expandedProjects` via `untracked()` to avoid
   * setting up a reactive dependency that would re-fire the effect on
   * every mutation.
   */
  private readonly autoExpandActiveFx = effect(() => {
    const name = this.activeProjectName();
    if (!name) return;
    if (this.lastAutoExpandedActive === name) return;
    this.lastAutoExpandedActive = name;
    untracked(() => {
      if (this._expandedProjects().has(name)) return;
      this._expandedProjects.update(set => {
        const next = new Set(set);
        next.add(name);
        this.writeExpandedProjects(next);
        return next;
      });
    });
  });

  /** All jobs, grouped under their project for the Explorer panel. */
  readonly grouped = this.jobService.grouped;
  readonly globalSearchTasks = computed(() => flattenGrouped(this.grouped()));

  /**
   * Concrete workspace shown in the titlebar breadcrumb. The breadcrumb no
   * longer exposes the generic create-workspace affordance or the active tab
   * kind; the project picker beside it carries the current project and runner
   * status.
   */
  readonly activeWorkspaceName = computed<string | null>(() => {
    const projectName = this.currentProjectName();
    if (!projectName) return null;
    const storage = this.projectStorageByName().get(projectName);
    const normalizedStorage = storage ? normalizeStorage(storage) : null;
    for (const ws of this.registryWorkspaces()) {
      const matched = ws.projects.some(p =>
        p.displayName === projectName ||
        (!!normalizedStorage && normalizeStorage(p.storageLocation) === normalizedStorage)
      );
      if (matched) return ws.displayName;
    }
    return null;
  });

  /** Project rows displayed in the titlebar pills + sidebar Explorer.
   *  A2 (2026-05-21): the visible "open jobs" counter excludes the
   *  archive lane. Archive grows monotonically with E2E fixtures /
   *  completed runs and was inflating the count (e.g. "Agent Software
   *  Studio: 447" → working set ~67 + ~380 archived). Operator now
   *  sees the working-set count by default; the picker dropdown can
   *  surface the full total separately when needed.
   *
   *  D5 follow-up: also include projects that the backend knows about
   *  but have zero working-set jobs (a fresh sandbox like
   *  `Playwright Test` lives entirely in 7-archive or has nothing yet —
   *  it must still render as a picker target for the probe to land
   *  tasks). The set of "known projects" comes from
   *  `TaskService.getWatchPaths()` via the shell's `projectNames` input
   *  the host passes in app.html. */
  readonly projectRows = computed<ProjectSidebarRow[]>(() => {
    return buildProjectSidebarRows(this.grouped(), this.knownProjectNames(), this.currentProjectName());
  });

  /** Row name → resolved storage path for the tree's rename-stable join. */
  readonly projectStorageByName = computed<ReadonlyMap<string, string>>(() => {
    const map = new Map<string, string>();
    for (const wp of this.projectWatchPaths()) {
      if (wp.name && wp.path) map.set(wp.name, wp.path);
    }
    return map;
  });

  /** Project display name → registry shortCode (e.g. "Agent Software Studio"
   *  → "ASS"), used to keep project-scoped tab titles short and scannable. */
  readonly projectShortCodeByName = computed<ReadonlyMap<string, string>>(() => {
    const map = new Map<string, string>();
    for (const ws of this.registryWorkspaces()) {
      for (const p of ws.projects) {
        if (p.displayName && p.shortCode) map.set(p.displayName, p.shortCode);
      }
    }
    return map;
  });

  /** Project name driving the currently open Board tab (or null when none). */
  readonly activeBoardProject = computed<string | null>(() => {
    const tab = this.activeTab();
    if (tab?.kind === 'board') return tab.projectName === '__all__' ? null : tab.projectName;
    return null;
  });

  readonly activeProjectSurface = computed<ExplorerProjectSurface | null>(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    if (tab.kind === 'board') return tab.projectName === '__all__' ? null : 'board';
    if (tab.kind === 'hub') return tab.section === 'wiki' ? 'wiki' : 'hub';
    if (tab.kind === 'epics') return tab.projectName === null ? null : 'epics';
    return null;
  });

  /**
   * The project the user is contextually "in" — drives the active titlebar
   * pill and the default project for sidebar CTAs. Board/Hub tabs name a
   * project directly; Task/Activity tabs resolve through the job index;
   * Diff/Welcome fall back to the last-known board project.
   */
  readonly currentProjectName = computed<string | null>(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    if (tab.kind === 'board') return tab.projectName === '__all__' ? null : tab.projectName;
    if (tab.kind === 'epics') return tab.projectName;
    if (tab.kind === 'hub') return tab.projectName;
    if (tab.kind === 'task' || tab.kind === 'activity') {
      const job = this.findJob(tab.taskKey);
      return job?.projectName ?? null;
    }
    return null;
  });

  readonly activityBarItems: readonly StudioActivityBarItem[] = [
    { key: 'explorer', icon: 'folder', label: 'Explorer' },
    { key: 'filters', icon: 'filter', label: 'Filters' },
    { key: 'cli', icon: 'cli', label: 'Agents / CLI' },
    { key: 'activity', icon: 'activity', label: 'Activity' },
    { key: 'runbook', icon: 'runbook', label: 'Runbook' },
  ];

  /**
   * Single source of truth for the ActivityBar's active marker (AGT-2042).
   * The sidebar toggle (`activePanel` + `sidebarVisible`) and the editor
   * route (`activeTab().kind`) used to light up buttons independently, so
   * two could be active at once. Funnelling both through one resolved key
   * makes the marker exclusive by construction — a button is active iff its
   * key equals this value.
   */
  readonly activeActivityKey = computed(() =>
    resolveActiveActivityKey({
      activeTabKind: this.activeTab()?.kind,
      activePanel: this.activePanel(),
      sidebarVisible: this.sidebarVisible(),
    }),
  );

  readonly activityBarBadgeCounts = computed<Record<string, number>>(() => ({
    filters: this.filterBadgeCount()
      ?? this.boardFilters.activeFilterCount(),
  }));

  openBoard(projectName: string): void {
    this.tabState.open({ kind: 'board', projectName });
  }

  /**
   * Activity-bar Epics button click. Opens or focuses the workspace-wide
   * epic overview as a normal editor tab.
   */
  onActivityBarOpenEpics(): void {
    this.tabState.open({ kind: 'epics', projectName: null });
  }

  /**
   * Whether any epic cards exist across all projects. Drives the
   * activity-bar Epics button visibility (hide-when-empty), so projects
   * that don't use epics never see the entry point.
   */
  readonly hasEpics = computed(() =>
    flattenGrouped(this.grouped()).some(t => t.kind === 'epic'),
  );

  openTask(job: TaskInfo): void {
    this.tabState.open({ kind: 'task', taskKey: job.taskKey });
    // Keep the legacy TaskSelectionService in sync so the embedded
    // <app-job-detail> can pick the job up by reading the selected signal.
    this.jobSelection.openDetail(job);
  }

  openHub(projectName: string): void {
    // Clicking "Project" is an explicit request for the Overview rail, even
    // when the Hub tab is already open on another section (e.g. Wiki). We
    // pass section=overview rather than leaving it undefined so the intent is
    // unambiguous and re-opening moves the rail back to Overview.
    this.tabState.open({ kind: 'hub', projectName, section: 'overview' });
  }

  /**
   * Explorer "Wiki" link for a single project. Opens (or focuses) the
   * project's Project Hub tab deep-linked to its Wiki rail, so the wiki is
   * reachable as a top-level sidebar item under Project Hub.
   */
  openWiki(projectName: string): void {
    this.tabState.open({ kind: 'hub', projectName, section: 'wiki' });
  }

  /**
   * Explorer "Epics" link for a single project (ASS-658). Opens the scoped
   * epic overview as a normal editor tab.
   */
  openProjectEpics(projectName: string): void {
    this.tabState.open({ kind: 'epics', projectName });
  }

  selectTab(key: string): void {
    this.tabState.select(key);
  }

  closeTab(key: string, event?: Event): void {
    event?.stopPropagation();
    this.tabState.close(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }

  closeOthers(key: string): void {
    this.tabState.closeOthers(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }
  closeRight(key: string): void {
    this.tabState.closeRight(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }
  closeLeft(key: string): void {
    this.tabState.closeLeft(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }
  closeAll(): void {
    this.tabState.closeAll();
    this.closeWorkspaceSettingsStateIfTabMissing();
  }

  private closeWorkspaceSettingsStateIfTabMissing(): void {
    if (!this.tabs().some(tab => studioTabKey(tab) === 'workspace-settings')) {
      this.workspaceOverlays.close();
    }
  }

  // ---- drag-reorder ---------------------------------------------------
  // Tracks which tab is currently being dragged and which tab the
  // pointer is hovering over so the template can render an insertion-
  // marker line + a "ghosted" source row.

  readonly draggingTabKey = signal<string | null>(null);
  /** Key the drop-marker is rendered before. `__end__` = after the last tab. */
  readonly dragOverTabKey = signal<string | null>(null);

  onTabDragStart(event: DragEvent, key: string): void {
    if (!event.dataTransfer) return;
    event.dataTransfer.effectAllowed = 'move';
    // The serialized payload isn't read back (we keep the source key in a
    // signal), but Firefox refuses to start a drag without setData.
    try { event.dataTransfer.setData('text/x-studio-tab', key); } catch { /* ignore */ }
    this.draggingTabKey.set(key);
  }

  onTabDragOver(event: DragEvent, overKey: string): void {
    if (!this.draggingTabKey()) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
    if (this.dragOverTabKey() !== overKey) {
      this.dragOverTabKey.set(overKey);
    }
  }

  onTabDragLeave(_event: DragEvent, overKey: string): void {
    if (this.dragOverTabKey() === overKey) {
      this.dragOverTabKey.set(null);
    }
  }

  onTabDrop(event: DragEvent, overKey: string): void {
    event.preventDefault();
    const source = this.draggingTabKey();
    this.draggingTabKey.set(null);
    this.dragOverTabKey.set(null);
    if (!source || source === overKey) return;
    this.tabState.move(source, overKey);
  }

  onTabDragEnd(event: DragEvent): void {
    void event;
    this.draggingTabKey.set(null);
    this.dragOverTabKey.set(null);
  }

  // ---- project drag-and-drop -----------------------------------------
  // F46: drag a project row onto a (different, real) workspace folder to
  // reassign the project's registry workspace membership. The drag
  // lifecycle + drop-validity live in `ProjectDragDropService`; the shell
  // owns the persistence because it already holds the registry-reload path.
  // No job folder is moved on disk — the registry is the source of truth
  // for the tree's grouping, so reloading it re-homes the row (ADR-0048).

  readonly projectDrag = inject(ProjectDragDropService);
  readonly moveErrorMessage = this.projectDrag.moveErrorMessage;

  onProjectWorkspaceDrop(e: { projectId: string; targetWorkspaceId: string }): void {
    this.projectDrag.movingProjectId.set(e.projectId);
    this.projectDrag.moveErrorMessage.set(null);
    this.jobService.updateRegistryProject(e.projectId, { workspaceId: e.targetWorkspaceId }).subscribe({
      next: () => {
        this.projectDrag.movingProjectId.set(null);
        this.reloadRegistryWorkspaces();
      },
      error: (err: unknown) => {
        this.projectDrag.movingProjectId.set(null);
        this.projectDrag.moveErrorMessage.set(this.errMsg(err));
      },
    });
  }

  /**
   * F46 — persist a workspace rename committed from the Explorer tree's inline
   * editor. Registry-only mutation (ADR-0048): no project folder is moved or
   * renamed on disk. Reload makes the new name visible in the tree header.
   */
  onTreeRenameWorkspace(e: { id: string; displayName: string }): void {
    this.registryWorkspaceBusyId.set(e.id);
    this.jobService.updateRegistryWorkspace(e.id, { displayName: e.displayName }).subscribe({
      next: () => { this.registryWorkspaceBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /** F46 step 7 — registry-only project rename (ADR-0042): PROJ id + on-disk
   *  storage untouched, so tasks/IDs/keys stay intact; reload shows it at once. */
  onTreeRenameProject(e: { projectId: string; displayName: string }): void {
    this.runProjectPatch(e.projectId, { displayName: e.displayName });
  }

  /**
   * F46 — destructive project delete from the tree's right-click menu, guarded
   * by two confirm stages (plain danger, then a type-to-confirm gate). Only
   * after both pass do we DELETE /api/projects/{projId}; the backend removes
   * storage folder + WatchPaths entry + registry record atomically (no orphan).
   */
  async onTreeDeleteProject(e: { projectId: string; displayName: string; shortCode: string | null }): Promise<void> {
    const label = e.displayName || e.projectId;
    const stageOne = await this.confirmDialog.confirm({
      title: 'Delete this project?',
      message: 'This permanently deletes the project and all of its tasks from disk. This cannot be undone.',
      detail: `${label} (${e.projectId})`,
      confirmLabel: 'Continue',
      cancelLabel: 'Cancel',
      kind: 'danger',
    });
    if (!stageOne) return;

    const typedValues = e.shortCode ? [e.displayName, e.shortCode] : [e.displayName];
    const hint = e.shortCode
      ? `Type the project name "${e.displayName}" or its code "${e.shortCode}" to confirm.`
      : `Type the project name "${e.displayName}" to confirm.`;
    const stageTwo = await this.confirmDialog.confirm({
      title: 'Confirm permanent deletion',
      message: 'Last step. Re-type the project to delete it and its entire storage folder.',
      requireTypedValues: typedValues,
      requireTypedPrompt: hint,
      confirmLabel: 'Delete project',
      cancelLabel: 'Cancel',
      kind: 'danger',
    });
    if (!stageTwo) return;

    this.registryProjectBusyId.set(e.projectId);
    this.jobService.deleteRegistryProject(e.projectId).subscribe({
      next: (res) => {
        this.registryProjectBusyId.set(null);
        this.reloadRegistryWorkspaces();
        // Host's WatchPaths reload purges stale board/hub tabs for the gone row.
        this.projectDeleted.emit();
        this.notifications.success(`Project "${res?.displayName ?? label}" deleted.`);
      },
      error: (err: unknown) => {
        this.registryProjectBusyId.set(null);
        const msg = this.errMsg(err);
        this.registryWorkspacesError.set(msg);
        this.notifications.error(`Failed to delete project "${label}": ${msg}`);
      },
    });
  }

  /** Drop into the empty trailing region of the tab strip → append. */
  onTabListDragOver(event: DragEvent): void {
    if (!this.draggingTabKey()) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
    if (this.dragOverTabKey() !== '__end__') {
      this.dragOverTabKey.set('__end__');
    }
  }

  onTabListDrop(event: DragEvent): void {
    event.preventDefault();
    const source = this.draggingTabKey();
    this.draggingTabKey.set(null);
    this.dragOverTabKey.set(null);
    if (!source) return;
    this.tabState.move(source, null);
  }

  /** Right-click context menu state. Coordinates are viewport-relative; the
   *  template positions an absolutely-placed menu at (x, y). One menu at a
   *  time — opening a new one replaces the previous. */
  readonly tabContextMenu = signal<{ key: string; x: number; y: number } | null>(null);

  openTabContextMenu(event: MouseEvent, key: string): void {
    event.preventDefault();
    this.tabContextMenu.set({ key, x: event.clientX, y: event.clientY });
  }

  closeTabContextMenu(): void {
    this.tabContextMenu.set(null);
  }

  // F23: shared <app-menu> driven via pure builders in studio-shell.menu-builders.ts.
  readonly tabCtxMenuItems = computed<readonly MenuItem[]>(() => {
    const ctx = this.tabContextMenu();
    if (!ctx) return [];
    const tabs = this.tabs();
    const idx = tabs.findIndex(t => studioTabKey(t) === ctx.key);
    const tab = idx >= 0 ? tabs[idx] : null;
    let task: { title: string; id: string; key?: string | null } | null = null;
    if (tab && (tab.kind === 'task' || tab.kind === 'activity')) {
      const job = this.findJob(tab.taskKey);
      if (job) task = { title: job.title || job.id, id: job.id, key: job.key };
    }
    return buildTabCtxMenuItems({
      totalTabs: tabs.length,
      hasTabsToRight: idx >= 0 && idx < tabs.length - 1,
      hasTabsToLeft: idx > 0,
      task,
    });
  });
  readonly tabCtxMenuPosition = computed(() => {
    const c = this.tabContextMenu();
    return c ? { x: c.x, y: c.y } : null;
  });
  onTabCtxMenuItemClick(ev: MenuItemClickEvent): void {
    const ctx = this.tabContextMenu();
    if (!ctx) return;
    if (ev.id === 'close') this.closeTab(ctx.key);
    else if (ev.id === 'close-others') this.closeOthers(ctx.key);
    else if (ev.id === 'close-right') this.closeRight(ctx.key);
    else if (ev.id === 'close-left') this.closeLeft(ctx.key);
    else if (ev.id === 'close-all') this.closeAll();
    else if (ev.id === 'copy-name' || ev.id === 'copy-id' || ev.id === 'copy-key') {
      this.handleTabCopyAction(ev.id, ctx.key);
    }
  }

  private handleTabCopyAction(action: string, tabKey: string): void {
    const tabs = this.tabs();
    const tab = tabs.find(t => studioTabKey(t) === tabKey);
    if (!tab || (tab.kind !== 'task' && tab.kind !== 'activity')) return;
    const job = this.findJob(tab.taskKey);
    if (!job) return;
    let text = '';
    let label = '';
    if (action === 'copy-name') { text = job.title || job.id; label = 'Name'; }
    else if (action === 'copy-id') { text = job.id; label = 'ID'; }
    else if (action === 'copy-key' && job.key) { text = job.key; label = 'Key'; }
    if (text) {
      copyTextToClipboard(text).then(ok => {
        if (ok) this.notifications.success(`${label} copied`);
      });
    }
  }
  readonly projectPickerItems = computed<readonly MenuItem[]>(() => buildProjectPickerItems({
    rows: this.projectRows(),
    totalProjectJobs: this.totalProjectJobs(),
    allProjectsActive: this.activeBoardProject() === null && this.activeTab()?.kind === 'board',
    activeTabKind: this.activeTab()?.kind,
  }));
  onProjectPickerItemClick(ev: MenuItemClickEvent): void {
    this.pickProject(ev.id === '__all__' ? null : ev.id);
  }

  togglePanel(panel: StudioActivityPanelKey | 'settings' | 'admin'): void {
    const visible = this.panelState.toggle(panel);
    if (panel === 'settings' && visible) {
      this.openWorkspaceSettingsTab();
    }
  }

  openWorkspaceSettingsTab(): void {
    this.workspaceOverlays.open(this.workspaceOverlays.section());
    this.tabState.open({ kind: 'workspace-settings' });
  }

  toggleTheme(): void {
    this.theme.update(t => (t === 'dark' ? 'light' : 'dark'));
  }

  setTheme(value: 'dark' | 'light'): void {
    this.theme.set(value);
  }

  setActivityBarSide(side: 'left' | 'right'): void {
    this.panelState.setActivityBarSide(side);
  }

  toggleChatRail(): void {
    this.panelState.toggleChatRail();
  }

  toggleCompactCards(): void {
    this.uiPrefs.toggleCompactCards();
  }

  setSettingsWorkspacesCollapsed(collapsed: boolean): void {
    this.settingsWorkspacesCollapsed.set(collapsed);
    if (typeof window === 'undefined') return;
    try {
      window.localStorage?.setItem(SETTINGS_WORKSPACES_COLLAPSED_KEY, collapsed ? '1' : '0');
    } catch {
      /* storage may be full / blocked */
    }
  }

  private readSettingsWorkspacesCollapsed(): boolean {
    if (typeof window === 'undefined') return true;
    try {
      return window.localStorage?.getItem(SETTINGS_WORKSPACES_COLLAPSED_KEY) !== '0';
    } catch {
      return true;
    }
  }

  clearAllFilters(): void {
    this.boardFilters.clearAllFilters();
  }

  /**
   * Hook for the "+ Workspace" titlebar button and the "+" icon next to
   * the Workspace group head in the Explorer. Opens the in-app
   * create-workspace dialog (POST /api/workspaces under the hood).
   */
  onAddWorkspace(): void {
    this.workspaceManager.openCreate();
  }

  onAddProjectToWorkspace(workspaceId: string): void {
    this.workspaceManager.openProjectOnboard(workspaceId);
  }

  /** Forces a fresh /api/tasks/grouped pull so the Explorer re-counts. */
  onRefreshWorkspace(): void {
    this.jobService.refresh();
  }

  /** Collapse every project row in the Explorer tree. */
  onCollapseAllProjects(): void {
    this._expandedProjects.set(new Set<string>());
    this.writeExpandedProjects(new Set<string>());
  }

  /**
   * Click handler for the Settings panel "Update stable now" /
   * "Check for updates" button. The label flips based on `behindBy()`:
   *
   *   - behindBy > 0   → "Update stable now"  → actually run the update
   *   - behindBy === 0 → "Check for updates"  → only POLL origin/main,
   *                                              don't kick off a stable
   *                                              re-checkout / restart.
   *
   * Previously this always called `trigger()`, so clicking "Check for
   * updates" immediately ran the full stable update pipeline — a
   * destructive action triggered by a button label that read like a
   * safe poll.
   */
  triggerUpdate(force = false): void {
    this.updateClient.openCenter();
    if (force || this.updateClient.behindBy() > 0) {
      void this.updateClient.trigger(null, force);
    } else {
      void this.updateClient.refreshNow();
    }
  }

  openUpdateCenter(): void {
    this.updateClient.openCenter();
    void this.updateClient.refreshNow();
  }

  /** Visible CLI types observed across the loaded jobs (for the CLI sidebar panel). */
  readonly cliRows = computed(() => {
    const jobs = this.jobService.jobs();
    const counts = new Map<string, number>();
    for (const j of jobs) {
      const t = j.cliType ?? 'unknown';
      counts.set(t, (counts.get(t) ?? 0) + 1);
    }
    return Array.from(counts.entries())
      .map(([cli, count]) => ({ cli, count, color: cliColorFor(cli) }))
      .sort((a, b) => b.count - a.count);
  });

  startSidebarResize(event: MouseEvent): void {
    event.preventDefault();
    const startX = event.clientX;
    const startW = this.sidebarWidth();
    const onMove = (e: MouseEvent) => {
      this.panelState.setSidebarWidth(startW + (e.clientX - startX));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'ew-resize';
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  /** Project's registry shortCode (e.g. "ASS") when known, else the full
   *  display name. Keeps project-scoped tab titles short and scannable while
   *  degrading cleanly for projects without a registry shortCode. */
  private projectShortLabel(projectName: string): string {
    return this.projectShortCodeByName().get(projectName) ?? projectName;
  }

  /** Resolve a hub tab's `section` to its rail item (label, icon). A missing
   *  or unknown section falls back to the default rail (`overview`). */
  private railItemForSection(section: string | undefined): ProjectRailItem {
    const key = isProjectRailKey(section) ? section : DEFAULT_PROJECT_RAIL_KEY;
    return (
      PROJECT_RAIL_ITEMS.find(i => i.key === key) ??
      PROJECT_RAIL_ITEMS.find(i => i.key === DEFAULT_PROJECT_RAIL_KEY)!
    );
  }

  /** Map a tab to its displayable label so the template stays terse. */
  tabLabel(tab: StudioTab): string {
    switch (tab.kind) {
      case 'board':
        return tab.projectName === '__all__' ? 'All projects · Board' : `${this.projectShortLabel(tab.projectName)} · Board`;
      case 'epics':
        return tab.projectName === null ? 'All projects · Epics' : `${this.projectShortLabel(tab.projectName)} · Epics`;
      case 'epic': {
        const labelKey = tab.viewTaskKey ?? tab.epicKey;
        const job = this.findJob(labelKey);
        return job?.title || job?.id || labelKey;
      }
      case 'task': {
        const job = this.findJob(tab.taskKey);
        return job?.title || job?.id || tab.taskKey;
      }
      case 'hub':
        return `${this.projectShortLabel(tab.projectName)} · ${this.railItemForSection(tab.section).label}`;
      case 'diff':
        return tab.commitSha;
      case 'activity': {
        const job = this.findJob(tab.taskKey);
        return `Activity · ${job?.title || tab.taskKey}`;
      }
      case 'workspace-settings':
        return 'Workspace settings';
      case 'welcome':
        return 'Welcome';
      default:
        return '';
    }
  }

  /** Marker for the tab list — used for the small chip on the left
   *  edge of the tab (e.g. `#90` for tasks). The hub / diff / activity
   *  tab labels already include the kind ("· Hub" / commit SHA /
   *  "Activity · …"), so we only render a leading num pill for
   *  task tabs where the `#order` adds info the title doesn't repeat. */
  tabNum(tab: StudioTab): string | null {
    if (tab.kind === 'task') {
      const job = this.findJob(tab.taskKey);
      if (!job) return null;
      return job.key || `#${job.order ?? '?'}`;
    }
    return null;
  }

  /** Leading icon for the tab strip. Hub tabs show their active section's
   *  rail icon (e.g. `book` for Wiki); other kinds render none here (the
   *  epic tab keeps its own dedicated glyph in the template). */
  tabIcon(tab: StudioTab): StudioIconName | null {
    if (tab.kind === 'hub') {
      return this.railItemForSection(tab.section).railIcon ?? null;
    }
    return null;
  }

  /**
   * AGT-2034 — resolve the owning project name for a tab so the tab strip
   * can paint the project's colour dot and keep the full name in the
   * tooltip. Returns `null` for tabs that do not belong to a single project
   * (All-projects board/epics, workspace settings, welcome, diff),
   * so no dot renders there.
   */
  private tabProjectName(tab: StudioTab): string | null {
    switch (tab.kind) {
      case 'board':
        return tab.projectName === '__all__' ? null : tab.projectName;
      case 'epics':
      case 'hub':
        return tab.projectName;
      case 'task':
      case 'activity':
        return this.findJob(tab.taskKey)?.projectName ?? null;
      case 'epic':
        return this.findJob(tab.viewTaskKey ?? tab.epicKey)?.projectName ?? null;
      default:
        return null;
    }
  }

  /**
   * AGT-2034 — project-identity colour for the tab's leading dot, or `null`
   * when the tab is not tied to a single project (so no dot renders). Reuses
   * the shared `projectIdentity()` palette — the same hue the Explorer tree
   * and board cards use — so a project's colour reads consistently across the
   * whole shell. No new colour source. The dot only encodes origin; the
   * shortCode (Board/Hub tabs) or task key (Task tabs) beside it stays the
   * primary text label, so colour is never the sole carrier (A11y).
   */
  tabDotColor(tab: StudioTab): string | null {
    const name = this.tabProjectName(tab);
    return name ? projectIdentity(name).color : null;
  }

  /**
   * AGT-2034 — native tooltip text for a tab. The visible label is shortened
   * to `SHORTCODE · Board` to save room; the hover restores the full project
   * name so the origin is never lost. Falls back to the plain label for tabs
   * with no owning project.
   */
  tabTooltip(tab: StudioTab): string {
    const name = this.tabProjectName(tab);
    const label = this.tabLabel(tab);
    return name ? `${name} — ${label}` : label;
  }

  private findJob(taskKey: string): TaskInfo | null {
    const grouped = this.grouped();
    for (const lane of Object.values(grouped)) {
      for (const job of lane as TaskInfo[]) {
        if (job.taskKey === taskKey) return job;
      }
    }
    return null;
  }

  /**
   * Map a tab to its underlying TaskInfo so the Open-Tabs hover popover
   * can render a TaskStatusCard. Returns `null` for board / hub / diff /
   * welcome tabs that do not correspond to a single task.
   */
  tabJob(tab: StudioTab): TaskInfo | null {
    if (tab.kind === 'task' || tab.kind === 'activity') return this.findJob(tab.taskKey);
    if (tab.kind === 'epic') return this.findJob(tab.viewTaskKey ?? tab.epicKey);
    return null;
  }
}
