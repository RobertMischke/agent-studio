import {
  ChangeDetectionStrategy,
  Component,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { JobInfo } from '../../models/job.model';
import { JobService } from '../../services/job.service';
import { ClientService } from '../../services/client.service';
import { FeatureFlagsService } from '../../services/feature-flags.service';
import { projectIdentity } from '../../services/project-identity.util';
import { JobSelectionService } from '../job-detail';
import { UiPreferencesService } from '../shell';
import { BoardFiltersService } from '../board';
import { StudioTabStateService } from './services/studio-tab-state.service';
import { StudioPanelStateService } from './services/studio-panel-state.service';
import {
  StudioTab,
  studioTabKey,
} from './studio-shell.types';

interface ProjectSidebarRow {
  name: string;
  initial: string;
  color: string;
  totalJobs: number;
  isActive: boolean;
}

/** Brand swatches per CLI — matches the status-bar glyph colours so the
 *  Sidebar CLI panel reads the same on first glance. */
function cliColorFor(cli: string): string {
  switch (cli) {
    case 'claude':  return '#d97757';
    case 'codex':   return '#569cd6';
    case 'copilot': return '#4ec9b0';
    case 'gemini':  return '#c586c0';
    default:        return '#6e6e6e';
  }
}

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
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './studio-shell.component.html',
  styleUrl: './studio-shell.component.scss',
})
export class StudioShellComponent {
  private readonly jobService = inject(JobService);
  readonly clientService = inject(ClientService);
  private readonly featureFlags = inject(FeatureFlagsService);
  private readonly tabState = inject(StudioTabStateService);
  private readonly panelState = inject(StudioPanelStateService);
  private readonly jobSelection = inject(JobSelectionService);
  readonly uiPrefs = inject(UiPreferencesService);
  readonly boardFilters = inject(BoardFiltersService);

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

  /**
   * Which Explorer-tree project rows are expanded (showing Board / Project
   * Hub / Activity sub-items). Persists across reloads so the user's
   * preferred tree shape survives an F5.
   */
  private readonly _expandedProjects = signal<Set<string>>(
    new Set(this.readExpandedProjects()),
  );
  readonly expandedProjects = this._expandedProjects.asReadonly();

  isProjectExpanded(name: string): boolean {
    return this._expandedProjects().has(name);
  }

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
  readonly openFilterSidesheet = output<void>();
  readonly openUsageSheet = output<void>();
  readonly openCliAdmin = output<void>();
  readonly openWorkspaceScreenshots = output<void>();
  readonly openOrchFeed = output<void>();
  readonly openOrchSettings = output<void>();

  /** Reflects the theme onto the document root so the design tokens flip. */
  private readonly themeFx = effect(() => {
    const t = this.theme();
    document.documentElement.dataset['studioTheme'] = t;
    localStorage.setItem('atp.studio.theme', t);
  });

  /** All jobs, grouped under their project for the Explorer panel. */
  readonly grouped = this.jobService.grouped;

  /** Project rows displayed in the titlebar pills + sidebar Explorer. */
  readonly projectRows = computed<ProjectSidebarRow[]>(() => {
    const grouped = this.grouped();
    const projects = new Map<string, number>();
    for (const lane of Object.values(grouped)) {
      for (const job of lane as JobInfo[]) {
        const name = job.projectName ?? '';
        projects.set(name, (projects.get(name) ?? 0) + 1);
      }
    }
    // Light up the pill for whichever project the active tab is contextually
    // "in" — board, hub, task, and activity tabs all map to a project.
    const active = this.currentProjectName();
    return Array.from(projects.entries())
      .map(([name, count]) => {
        const id = projectIdentity(name);
        return {
          name,
          initial: id.initial,
          color: id.color,
          totalJobs: count,
          isActive: active === name,
        };
      })
      .sort((a, b) => a.name.localeCompare(b.name));
  });

  /** Project name driving the currently open Board tab (or null when none). */
  readonly activeBoardProject = computed<string | null>(() => {
    const tab = this.activeTab();
    if (tab?.kind === 'board') return tab.projectName === '__all__' ? null : tab.projectName;
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
    if (tab.kind === 'hub') return tab.projectName;
    if (tab.kind === 'task' || tab.kind === 'activity') {
      const job = this.findJob(tab.jobKey);
      return job?.projectName ?? null;
    }
    return null;
  });

  /** Activity-bar entries — matches the reference layout order. */
  readonly activityBarItems = [
    { key: 'explorer' as const, icon: 'folder', label: 'Explorer' },
    { key: 'tasks' as const, icon: 'list', label: 'Tasks' },
    { key: 'filters' as const, icon: 'filter', label: 'Filters' },
    { key: 'cli' as const, icon: 'cli', label: 'Agents / CLI' },
    { key: 'activity' as const, icon: 'activity', label: 'Activity' },
    { key: 'runbook' as const, icon: 'runbook', label: 'Runbook' },
  ];

  openBoard(projectName: string): void {
    this.tabState.open({ kind: 'board', projectName });
  }

  openTask(job: JobInfo): void {
    this.tabState.open({ kind: 'task', jobKey: job.jobKey });
    // Keep the legacy JobSelectionService in sync so the embedded
    // <app-job-detail> can pick the job up by reading the selected signal.
    this.jobSelection.openDetail(job);
  }

  openHub(projectName: string): void {
    this.tabState.open({ kind: 'hub', projectName });
  }

  selectTab(key: string): void {
    this.tabState.select(key);
  }

  closeTab(key: string, event?: MouseEvent): void {
    event?.stopPropagation();
    this.tabState.close(key);
  }

  closeOthers(key: string): void { this.tabState.closeOthers(key); }
  closeRight(key: string): void { this.tabState.closeRight(key); }
  closeLeft(key: string): void { this.tabState.closeLeft(key); }
  closeAll(): void { this.tabState.closeAll(); }

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

  onTabDragEnd(_event: DragEvent): void {
    this.draggingTabKey.set(null);
    this.dragOverTabKey.set(null);
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

  /** Helpers the context menu uses to enable/disable Close-to-X items. */
  tabIndexOf(key: string): number {
    return this.tabs().findIndex(t => studioTabKey(t) === key);
  }

  hasTabsToRightOf(key: string): boolean {
    const idx = this.tabIndexOf(key);
    return idx >= 0 && idx < this.tabs().length - 1;
  }

  hasTabsToLeftOf(key: string): boolean {
    return this.tabIndexOf(key) > 0;
  }

  togglePanel(panel: typeof this.activityBarItems[number]['key'] | 'settings'): void {
    this.panelState.toggle(panel);
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

  clearAllFilters(): void {
    this.boardFilters.clearAllFilters();
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

  /** Lane-state counts across the active filter set (for the Tasks panel summary). */
  readonly laneSummary = computed(() => {
    const grouped = this.grouped();
    const len = (k: keyof typeof grouped) => (grouped[k] ?? []).length;
    return {
      backlog: len('backlog') + len('preparation') + len('orchestratorPrep') + len('needsHumanReview'),
      ready: len('ready'),
      progress: len('progress'),
      autoReview: len('autoReview'),
      humanReview: len('humanReview'),
      completed: len('completed'),
      archive: len('archive'),
    };
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

  /** Map a tab to its displayable label so the template stays terse. */
  tabLabel(tab: StudioTab): string {
    switch (tab.kind) {
      case 'board':
        return tab.projectName === '__all__' ? 'All projects · Board' : `${tab.projectName} · Board`;
      case 'task': {
        const job = this.findJob(tab.jobKey);
        return job?.title || job?.id || tab.jobKey;
      }
      case 'hub':
        return `${tab.projectName} · Hub`;
      case 'diff':
        return tab.commitSha;
      case 'activity': {
        const job = this.findJob(tab.jobKey);
        return `Activity · ${job?.title || tab.jobKey}`;
      }
      case 'welcome':
        return 'Welcome';
    }
  }

  /** Marker for the tab list — used for #N pills on task tabs. */
  tabNum(tab: StudioTab): string | null {
    if (tab.kind === 'task') {
      const job = this.findJob(tab.jobKey);
      return job ? `#${job.order ?? '?'}` : null;
    }
    if (tab.kind === 'hub') return 'hub';
    if (tab.kind === 'diff') return 'diff';
    if (tab.kind === 'activity') return 'log';
    return null;
  }

  private findJob(jobKey: string): JobInfo | null {
    const grouped = this.grouped();
    for (const lane of Object.values(grouped)) {
      for (const job of lane as JobInfo[]) {
        if (job.jobKey === jobKey) return job;
      }
    }
    return null;
  }
}
