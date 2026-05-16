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

  /** Drives the "moon / sun" icon in the titlebar. */
  readonly theme = signal<'dark' | 'light'>(
    (localStorage.getItem('atp.studio.theme') as 'dark' | 'light' | null) ?? 'dark',
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
    const active = this.activeBoardProject();
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
